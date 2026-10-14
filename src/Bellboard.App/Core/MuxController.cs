using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bellboard.Panes;
using Bellboard.Services;

namespace Bellboard.Core;

/// <summary>Owns workspaces, their split trees, focus, and pane lifecycle. UI-thread only.</summary>
public sealed class MuxController
{
    public ObservableCollection<Workspace> Workspaces { get; } = new();
    public Workspace? Selected { get; private set; }
    public MainWindow Window { get; }
    public BellboardConfig Config { get; }
    public string PipeName { get; }
    public NotificationCenter Notifications { get; private set; } = null!;
    public Monitors? Monitors { get; private set; }
    public TerminalViewPool TerminalPool { get; private set; } = null!;
    public ShellPool Shells { get; private set; } = null!;
    public AgentManager Agents { get; }
    /// <summary>State changes pushed to pipe subscribers (the remote sidecar, `bell events`).</summary>
    public EventHub Events { get; private set; } = null!;
    /// <summary>Claude Code permission requests waiting for an answer from a phone.</summary>
    public PromptBroker Prompts { get; }
    /// <summary>Bellboard Remote: the phone view's sidecar, paired devices, away detection.</summary>
    public RemoteHost Remote { get; private set; } = null!;
    private string? _fontStack;
    /// <summary>Effective terminal font stack (config + Windows Terminal font + Nerd Font fallback).</summary>
    public string FontStack => _fontStack ??= FontDetector.Resolve(Config.FontFamily, BellboardConfig.DefaultFontStack);

    /// <summary>Recently closed workspaces and panes (Ctrl+Shift+Z reopens), newest last.</summary>
    public List<ClosedItem> Closed { get; } = new();

    /// <summary>Most recently used folders (for the "open folder" menu), newest first.</summary>
    public List<string> RecentDirs { get; } = new();
    /// <summary>Browser history for the start page, newest first.</summary>
    public List<(string Url, string Title)> BrowserHistory { get; } = new();

    private int _wsCounter, _paneCounter;
    // Re-parenting WebView2s during a re-layout fires stray focus events; ignore them briefly.
    private PaneBase? _focusTarget;
    private DateTime _focusGuardUntil;

    public MuxController(MainWindow window, BellboardConfig config, string pipeName)
    {
        Window = window;
        Config = config;
        PipeName = pipeName;
        Agents = new AgentManager(this);
        Prompts = new PromptBroker(this);
        Workspaces.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Workspaces.Count; i++)
            {
                Workspaces[i].Index = i + 1;
                Workspaces[i].Refresh();
            }
        };
    }

    public void Start()
    {
        Notifications = new NotificationCenter(this);
        Monitors = new Monitors(this, Window.Dispatcher);
        TerminalPool = new TerminalViewPool(Window.WarmHost,
            () => System.Drawing.ColorTranslator.FromHtml(Config.Theme["background"]));
        Shells = new ShellPool(this);
        Events = new EventHub(this, Window.Dispatcher);
        Remote = new RemoteHost(this, Window.Dispatcher);
    }

    /// <summary>Apply new settings to the running app (from the Settings window or a config.json edit).</summary>
    public void ApplyConfig(BellboardConfig updated)
    {
        Config.CopyFrom(updated);
        _fontStack = null;
        var bg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Config.Theme["background"]);
        foreach (var pane in Workspaces.SelectMany(w => w.Panes))
        {
            pane.Host.Background = new System.Windows.Media.SolidColorBrush(bg);
            if (pane is TerminalPane t) t.ApplyConfig();
        }
        if (!Config.PrestartShell) Shells.Dispose();
        Remote.Apply();
        Events.MarkDirty();
        Log.Info("settings applied");
    }

    /// <summary>Folder for a new workspace, per "newWorkspaceDirectory" (default: the focused terminal's folder).</summary>
    public string? DefaultNewWorkspaceDir()
    {
        var setting = Config.NewWorkspaceDirectory?.Trim() ?? "inherit";
        if (setting.Equals("home", StringComparison.OrdinalIgnoreCase)) return null;
        if (setting.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            var cwd = (Selected?.FocusedPane as TerminalPane)?.Cwd ?? Selected?.Cwd;
            return cwd != null && System.IO.Directory.Exists(cwd) ? cwd : null;
        }
        return System.IO.Directory.Exists(setting) ? setting : null;
    }

    public void RememberDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.StartsWith('/')) return;
        RecentDirs.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        RecentDirs.Insert(0, dir);
        if (RecentDirs.Count > 15) RecentDirs.RemoveRange(15, RecentDirs.Count - 15);
    }

    /// <summary>SSH targets typed in "Connect to a host…", newest first (shown in the shell menu).</summary>
    public List<string> RecentSsh { get; } = new();

    public void RememberSsh(string target)
    {
        target = target.Trim();
        if (target.Length == 0) return;
        RecentSsh.RemoveAll(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
        RecentSsh.Insert(0, target);
        if (RecentSsh.Count > 8) RecentSsh.RemoveRange(8, RecentSsh.Count - 8);
    }

    public void RememberPage(string url, string? title)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return;
        var existing = BrowserHistory.FindIndex(h => h.Url == url);
        var keepTitle = existing >= 0 ? BrowserHistory[existing].Title : "";
        if (existing >= 0) BrowserHistory.RemoveAt(existing);
        BrowserHistory.Insert(0, (url, string.IsNullOrWhiteSpace(title) ? keepTitle : title!));
        if (BrowserHistory.Count > 60) BrowserHistory.RemoveRange(60, BrowserHistory.Count - 60);
    }

    // ---- Workspaces ----

    /// <summary>Ids for a shell started before its workspace exists (the ShellPool).</summary>
    public (string WorkspaceId, string PaneId) ReserveIds() => ("w" + ++_wsCounter, "p" + ++_paneCounter);

    public Workspace AddWorkspace(string? name, string? id = null)
    {
        var ws = new Workspace(id ?? "w" + ++_wsCounter) { CustomName = string.IsNullOrWhiteSpace(name) ? null : name };
        ws.Container.Visibility = Visibility.Collapsed;
        Window.ContentHost.Children.Add(ws.Container);
        Workspaces.Add(ws);
        return ws;
    }

    public Workspace NewWorkspace(string? name = null, string? cwd = null, string? command = null, bool select = true, string? shell = null,
        Dictionary<string, string>? extraEnv = null)
    {
        var pre = Shells?.Take(cwd, shell, command, extraEnv != null);
        var ws = AddWorkspace(name, pre?.WorkspaceId);
        var pane = CreateTerminal(ws, cwd, command, shell: shell, extraEnv: extraEnv, prewarmed: pre);
        ws.Root = new PaneStack(this, pane);
        ws.FocusedPane = pane;
        Rebuild(ws);
        _ = InitPane(pane);
        if (select || Selected == null) Select(ws);
        Notifications.UpdateCounts();
        Shells?.Refill();
        return ws;
    }

    /// <summary>A workspace holding one feed pane (see TerminalPane.Feed), listed after the others; not selected unless asked.</summary>
    public TerminalPane NewFeed(string name, string? cwd, string? badge, string? label, bool select)
    {
        var ws = AddWorkspace(name);
        var pane = new TerminalPane(this, ws, "p" + ++_paneCounter, TerminalPool.Take())
        {
            Feed = true,
            FeedBadge = badge,
            FeedLabel = label,
            Cwd = cwd,
        };
        ws.Root = new PaneStack(this, pane);
        ws.FocusedPane = pane;
        Rebuild(ws);
        _ = InitPane(pane);
        if (select || Selected == null) Select(ws);
        Notifications.UpdateCounts();
        return pane;
    }

    public void Select(Workspace ws)
    {
        if (!Workspaces.Contains(ws)) return;
        if (Selected != ws)
        {
            var previous = Selected;
            foreach (var w in Workspaces)
            {
                w.IsSelected = w == ws;
                w.Container.Visibility = w == ws ? Visibility.Visible : Visibility.Collapsed;
            }
            Selected = ws;
            Window.SyncSelection(ws);
            if (previous != null)
            {
                previous.HiddenSince = DateTime.Now;
                SetMemoryLevel(previous, low: true);
            }
            SetMemoryLevel(ws, low: false);
            foreach (var t in ws.Panes.OfType<TerminalPane>().Where(t => t.Unloaded)) _ = t.ReloadAsync();
        }
        if ((ws.FocusedPane ?? ws.Panes.FirstOrDefault()) is { } target) Focus(target);
        Window.UpdateTitle();
    }

    public void SelectRelative(int delta)
    {
        if (Workspaces.Count == 0) return;
        var i = Selected == null ? 0 : Workspaces.IndexOf(Selected);
        Select(Workspaces[((i + delta) % Workspaces.Count + Workspaces.Count) % Workspaces.Count]);
    }

    /// <param name="quitIfLast">Closing the last workspace quits Bellboard, unless false (then a fresh one opens).</param>
    public void CloseWorkspace(Workspace ws, bool quitIfLast = true, bool remember = true)
    {
        var idx = Workspaces.IndexOf(ws);
        if (idx < 0) return;
        if (remember && ws.Root != null && SessionStore.CaptureLayout(ws.Root) is { } closedLayout)
        {
            try { Remember(new ClosedItem("workspace", ws.Name, ws.CustomName, closedLayout, ws.Id, ws.AgentTask, idx)); }
            catch (Exception ex) { Log.Error("capturing a closed workspace", ex); }
        }
        if (!quitIfLast && Workspaces.Count == 1) NewWorkspace(cwd: ws.AgentTask?.Repo ?? ws.Cwd);
        foreach (var p in ws.Panes.ToList())
        {
            Notifications.Forget(p);
            p.Dispose();
        }
        ws.Container.Children.Clear();
        Window.ContentHost.Children.Remove(ws.Container);
        Workspaces.Remove(ws);
        if (Workspaces.Count == 0)
        {
            Selected = null;
            Window.Close();
            return;
        }
        if (Selected == ws)
        {
            Selected = null;
            Select(Workspaces[Math.Clamp(idx, 0, Workspaces.Count - 1)]);
        }
        Notifications.UpdateCounts();
    }

    public Workspace? FindWorkspace(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "current") return Selected;
        var byId = Workspaces.FirstOrDefault(w => w.Id == key);
        if (byId != null) return byId;
        if (int.TryParse(key, out var n) && n >= 1 && n <= Workspaces.Count) return Workspaces[n - 1];
        return Workspaces.FirstOrDefault(w => string.Equals(w.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Panes ----

    public TerminalPane CreateTerminal(Workspace ws, string? cwd, string? command, string? scrollback = null, AgentInfo? agent = null, string? shell = null,
        Dictionary<string, string>? extraEnv = null, PrewarmedShell? prewarmed = null) =>
        new(this, ws, prewarmed?.PaneId ?? "p" + ++_paneCounter, TerminalPool.Take())
        {
            Prewarmed = prewarmed,
            Shell = shell,
            ExtraEnv = extraEnv,
            StartCwd = cwd,
            StartCommand = command,
            RestoreScrollback = scrollback,
            RestoreAgent = agent,
        };

    public BrowserPane CreateBrowser(Workspace ws, string? url) =>
        new(this, ws, "p" + ++_paneCounter) { InitialUrl = string.IsNullOrWhiteSpace(url) ? null : BrowserPane.NormalizeUrl(url) };

    public async Task InitPane(PaneBase pane)
    {
        try
        {
            await pane.InitializeAsync();
            if (Selected == pane.Workspace && pane.Workspace.FocusedPane == pane && Window.IsActive) pane.FocusContent();
        }
        catch (Exception ex)
        {
            Log.Error($"pane {pane.Id}: initialization failed", ex);
        }
    }

    public PaneBase? FindPane(string? id) =>
        string.IsNullOrEmpty(id) ? null : Workspaces.SelectMany(w => w.Panes).FirstOrDefault(p => p.Id == id);

    public PaneBase Split(PaneBase target, bool horizontal, PaneBase newPane)
    {
        var ws = target.Workspace;
        ws.ZoomedPane = null;
        Insert(ws, target.Stack, new PaneStack(this, newPane), horizontal ? DropSide.Right : DropSide.Down);
        Rebuild(ws);
        _ = InitPane(newPane);
        Focus(newPane);
        Monitors?.Poke();
        return newPane;
    }

    public TerminalPane SplitTerminal(PaneBase target, bool horizontal, string? cwd = null, string? command = null, string? shell = null)
    {
        // A split inherits the folder and, unless told otherwise, the shell of the pane it splits.
        var pane = CreateTerminal(target.Workspace, cwd ?? target.Cwd, command,
            shell: shell ?? ShellFrom(target));
        Split(target, horizontal, pane);
        return pane;
    }

    public BrowserPane OpenBrowser(PaneBase near, string? url, bool reuse = true)
    {
        if (reuse && near.Workspace.Panes.OfType<BrowserPane>().FirstOrDefault() is { } existing)
        {
            if (!string.IsNullOrWhiteSpace(url)) existing.Navigate(url);
            Focus(existing);
            return existing;
        }
        var b = CreateBrowser(near.Workspace, url);
        Split(near, true, b);
        return b;
    }

    public void OpenLink(TerminalPane from, string url, bool external)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        if (external)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            return;
        }
        OpenBrowser(from, uri.AbsoluteUri);
    }

    public void ClosePane(PaneBase pane)
    {
        var ws = pane.Workspace;
        if (!Workspaces.Contains(ws) || ws.FindPane(pane.Id) == null) return;
        var stack = pane.Stack;
        if (ws.PaneCount > 1 && pane is not TerminalPane { Feed: true })
        {
            try { Remember(new ClosedItem("pane", pane.TabLabel, null, SessionStore.CapturePaneLayout(pane), ws.Id, null, 0)); }
            catch (Exception ex) { Log.Error("capturing a closed pane", ex); }
        }
        Notifications.Forget(pane);
        if (stack.Tabs.Count > 1)
        {
            // Closing one tab of several: the stack stays where it is.
            stack.Remove(pane);
            pane.Dispose();
            if (ws.FocusedPane == pane) Focus(stack.Active);
            ws.Refresh();
            return;
        }
        if (stack.Parent == null)
        {
            CloseWorkspace(ws);
            return;
        }
        if (ws.ZoomedPane?.Stack == stack) ws.ZoomedPane = null;
        var sibling = RemoveStack(ws, stack)!;
        pane.Dispose();
        Rebuild(ws);
        if (ws.FocusedPane == pane || ws.FocusedPane == null)
        {
            var next = FirstLeaf(sibling).Active;
            ws.FocusedPane = next;
            if (ws == Selected) Focus(next);
        }
        ws.Refresh();
    }

    private static PaneStack FirstLeaf(Node n) => n is PaneStack p ? p : FirstLeaf(((SplitNode)n).A);

    /// <summary>Takes an emptied stack out of the split tree; its sibling takes the parent split's place (returned).</summary>
    private static Node? RemoveStack(Workspace ws, PaneStack stack)
    {
        PaneStack.Detach(stack.Host);
        stack.StopAnimations();
        var parent = stack.Parent;
        if (parent == null)
        {
            ws.Root = null;
            return null;
        }
        var sibling = parent.A == stack ? parent.B : parent.A;
        var grand = parent.Parent;
        sibling.Parent = grand;
        if (grand == null) ws.Root = sibling;
        else if (grand.A == parent) grand.A = sibling;
        else grand.B = sibling;
        return sibling;
    }

    /// <summary>Puts <paramref name="added"/> on one side of <paramref name="target"/>, splitting the space it had.</summary>
    private static SplitNode Insert(Workspace ws, Node target, PaneStack added, DropSide side)
    {
        var parent = target.Parent;
        bool before = side is DropSide.Left or DropSide.Up;
        var split = new SplitNode
        {
            Horizontal = side is DropSide.Left or DropSide.Right,
            A = before ? added : target,
            B = before ? target : added,
            Parent = parent,
        };
        if (parent == null) ws.Root = split;
        else if (parent.A == target) parent.A = split;
        else parent.B = split;
        target.Parent = split;
        added.Parent = split;
        return split;
    }

    // ---- Pins ----

    /// <summary>Pins or unpins a workspace; pinned ones are listed first, in the order they were pinned.</summary>
    public void SetPinned(Workspace ws, bool pinned)
    {
        if (ws.Pinned == pinned || !Workspaces.Contains(ws)) return;
        int others = Workspaces.Count(w => w.Pinned && w != ws);
        ws.Pinned = pinned;
        // Pinning puts it last among the pinned; unpinning, first among the rest. Either way: index `others`.
        int from = Workspaces.IndexOf(ws);
        if (from != others) Workspaces.Move(from, others);
        ws.Refresh();
        Window.SyncSelection(Selected ?? ws);
    }

    /// <summary>Pins or unpins a tab; pinned tabs sit first in their row.</summary>
    public void SetPinned(PaneBase pane, bool pinned)
    {
        if (pane.Pinned == pinned) return;
        var tabs = pane.Stack.Tabs;
        tabs.Remove(pane);
        pane.Pinned = pinned;
        // Last of the pinned when pinning; first of the rest when unpinning.
        tabs.Insert(tabs.Count(t => t.Pinned), pane);
        pane.Stack.Refresh();
        pane.Workspace.Refresh();
    }

    /// <summary>Why closing this pane is refused (a pinned tab, or the last pane of a pinned workspace), or null.</summary>
    public static string? PinnedReason(PaneBase pane) =>
        pane.Pinned ? (pane.Stack.Tabs.Count > 1 ? "This tab is pinned. Unpin it to close it." : "This pane is pinned. Unpin it to close it.")
        : pane.Workspace.Pinned && pane.Workspace.PaneCount == 1 ? "This workspace is pinned. Unpin it to close it."
        : null;

    /// <summary>Closing from a button, key, menu or the CLI: pinned things stay, with a hint that says why.</summary>
    public bool RequestClose(PaneBase pane)
    {
        if (PinnedReason(pane) is { } why)
        {
            if (Selected == pane.Workspace) Window.ShowHint(pane.Stack.HeaderElement, why);
            return false;
        }
        ClosePane(pane);
        return true;
    }

    public bool RequestCloseWorkspace(Workspace ws, UIElement? near = null)
    {
        if (ws.Pinned)
        {
            Window.ShowHint(near ?? (UIElement?)ws.FocusedPane?.Stack.HeaderElement ?? Window.ContentHost, "This workspace is pinned. Unpin it to close it.");
            return false;
        }
        CloseWorkspace(ws);
        return true;
    }

    /// <summary>A pinned terminal whose shell ended gets a fresh shell in its place, folder and shell.</summary>
    public void RestartTerminal(TerminalPane old)
    {
        var ws = old.Workspace;
        var stack = old.Stack;
        if (!Workspaces.Contains(ws) || !stack.Tabs.Contains(old)) return;
        var fresh = CreateTerminal(ws, old.Cwd ?? old.StartCwd, null, shell: old.Shell);
        fresh.Pinned = old.Pinned;
        stack.Add(fresh, stack.Tabs.IndexOf(old));
        Notifications.Forget(old);
        stack.Remove(old);
        old.Dispose();
        if (ws.FocusedPane == old) ws.FocusedPane = fresh;
        if (ws.ZoomedPane == old) ws.ZoomedPane = fresh;
        _ = InitPane(fresh);
        if (Selected == ws) Focus(fresh);
        ws.Refresh();
    }

    // ---- Moving panes (their shells and pages keep running) ----

    /// <summary>
    /// Why this pane can't leave its workspace, or null: an agent workspace keeps a pane (its worktree and branch belong
    /// to it), and so does a pinned one.
    /// </summary>
    public static string? StayReason(PaneBase pane) =>
        pane.Workspace.PaneCount > 1 ? null
        : pane.Workspace.AgentTask != null ? "An agent workspace keeps its last pane"
        : pane.Workspace.Pinned ? "A pinned workspace keeps its last pane"
        : null;

    public static bool CanLeave(PaneBase pane) => StayReason(pane) == null;

    /// <summary>Takes a pane out of its tab group without closing it; an emptied group leaves the split tree.</summary>
    private static void Lift(PaneBase pane)
    {
        var stack = pane.Stack;
        if (!stack.Remove(pane)) RemoveStack(pane.Workspace, stack);
    }

    /// <summary>
    /// Moves a pane next to <paramref name="target"/>: into its tab group (at <paramref name="index"/>), or splitting
    /// the target's spot on one side. Works across workspaces. Returns false when there is nothing to do.
    /// </summary>
    public bool MovePane(PaneBase pane, PaneBase target, DropSide side, int? index = null)
    {
        var from = pane.Workspace;
        var to = target.Workspace;
        var stack = target.Stack;
        if (!Workspaces.Contains(from) || !Workspaces.Contains(to)) return false;
        if (stack == pane.Stack)
        {
            if (side == DropSide.Tab)
            {
                // Same tab group: reorder.
                var tabs = stack.Tabs;
                int old = tabs.IndexOf(pane);
                int at = Math.Clamp(index ?? tabs.Count, 0, tabs.Count);
                if (at > old) at--;
                if (at == old) return false;
                tabs.RemoveAt(old);
                tabs.Insert(at, pane);
                stack.SortPinned();
                stack.Refresh();
                return true;
            }
            if (stack.Tabs.Count == 1) return false;
        }
        if (from != to && !CanLeave(pane)) return false;
        from.ZoomedPane = null;
        to.ZoomedPane = null;
        Lift(pane);
        pane.Workspace = to;
        if (side == DropSide.Tab)
        {
            stack.Add(pane, index);
            stack.SortPinned();
        }
        else Insert(to, stack, new PaneStack(this, pane), side);
        AfterMove(from, to, pane);
        return true;
    }

    /// <summary>Moves a pane into another workspace (to the right of what is there), or into a new one when <paramref name="to"/> is null.</summary>
    public Workspace? MoveToWorkspace(PaneBase pane, Workspace? to)
    {
        var from = pane.Workspace;
        if (to == from || (to != null && (to.Root == null || !Workspaces.Contains(to)))) return null;
        if (to == null && from.PaneCount == 1) return null;
        if (!CanLeave(pane)) return null;
        from.ZoomedPane = null;
        Lift(pane);
        if (to == null)
        {
            to = AddWorkspace(null);
            int below = Math.Max(Workspaces.IndexOf(from) + 1, Workspaces.Count(w => w.Pinned));
            Workspaces.Move(Workspaces.Count - 1, Math.Min(below, Workspaces.Count - 1));
            pane.Workspace = to;
            to.Root = new PaneStack(this, pane);
        }
        else
        {
            to.ZoomedPane = null;
            pane.Workspace = to;
            int columns = Slots(to.Root!, true);
            Insert(to, to.Root!, new PaneStack(this, pane), DropSide.Right).Ratio = columns / (columns + 1.0);
        }
        to.FocusedPane = pane;
        AfterMove(from, to, pane);
        return to;
    }

    private void AfterMove(Workspace from, Workspace to, PaneBase pane)
    {
        if (from != to)
        {
            if (from.Root == null) CloseWorkspace(from, remember: false);
            else
            {
                if (from.FocusedPane == pane || from.FocusedPane?.Workspace != from) from.FocusedPane = FirstLeaf(from.Root).Active;
                Rebuild(from);
                from.Refresh();
            }
        }
        Rebuild(to);
        Focus(pane);
        to.Refresh();
        Notifications.UpdateCounts();
        Window.UpdateTitle();
        Monitors?.Poke();
    }

    /// <summary>Turns the split holding this pane from side by side into top and bottom, or back.</summary>
    public bool FlipSplit(PaneBase pane)
    {
        if (pane.Stack.Parent is not { } split) return false;
        pane.Workspace.ZoomedPane = null;
        split.Horizontal = !split.Horizontal;
        Rebuild(pane.Workspace);
        Focus(pane);
        Window.UpdateTitle();
        return true;
    }

    /// <summary>Swaps the two sides of the split holding this pane.</summary>
    public bool SwapSides(PaneBase pane)
    {
        if (pane.Stack.Parent is not { } split) return false;
        pane.Workspace.ZoomedPane = null;
        (split.A, split.B) = (split.B, split.A);
        split.Ratio = 1 - split.Ratio;
        Rebuild(pane.Workspace);
        Focus(pane);
        Window.UpdateTitle();
        return true;
    }

    // ---- Memory for hidden workspaces ----

    /// <summary>Hidden workspaces' views ask WebView2 to trim memory (measured: ~half the working set).</summary>
    private static void SetMemoryLevel(Workspace ws, bool low)
    {
        foreach (var p in ws.Panes)
        {
            try
            {
                if (p.Web.CoreWebView2 is { } core)
                    core.MemoryUsageTargetLevel = low ? Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low
                                                      : Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch { }
        }
    }

    /// <summary>Unload terminal renderers in workspaces hidden longer than the configured idle time.</summary>
    public async Task UnloadIdleAsync(bool force = false, Workspace? only = null)
    {
        int minutes = Config.UnloadIdleWorkspacesMinutes;
        if (minutes <= 0 && !force) return;
        foreach (var ws in Workspaces.ToList())
        {
            if (ws == Selected || (only != null && ws != only)) continue;
            if (!force && DateTime.Now - ws.HiddenSince < TimeSpan.FromMinutes(minutes)) continue;
            foreach (var t in ws.Panes.OfType<TerminalPane>().Where(t => !t.Unloaded).ToList()) await t.UnloadAsync();
        }
    }

    // ---- Broadcast ----

    public void SetBroadcast(Workspace ws, bool on)
    {
        ws.Broadcast = on;
        var i = ws.Status.FindIndex(s => s.Key == "broadcast");
        if (on && i < 0) ws.Status.Insert(0, new StatusPill("broadcast", "broadcasting input", "yellow"));
        if (!on && i >= 0) ws.Status.RemoveAt(i);
        foreach (var s in ws.Stacks) s.Refresh();
        ws.Refresh();
        Window.UpdateTitle();
    }

    // ---- Reopen closed / reorder ----

    private void Remember(ClosedItem item)
    {
        Closed.Add(item);
        if (Closed.Count > 20) Closed.RemoveAt(0);
    }

    /// <summary>Reopen the most recently closed workspace or pane (shells start fresh in their old folders).</summary>
    public string? ReopenClosed()
    {
        if (Closed.Count == 0) return null;
        var item = Closed[^1];
        Closed.RemoveAt(Closed.Count - 1);
        if (item.Kind == "pane" && FindWorkspace(item.WorkspaceId) is { } home && home.FocusedPane is { } near && home.Id == item.WorkspaceId)
        {
            var node = SessionStore.Build(this, home, item.Layout, null);
            var pane = ((PaneStack)node).Active;
            Split(near, true, pane);
            return $"reopened {item.Label} in {home.Name}";
        }
        var ws = AddWorkspace(item.CustomName);
        ws.AgentTask = item.Agent is { } a && System.IO.Directory.Exists(a.Worktree) ? a : null;
        ws.Root = SessionStore.Build(this, ws, item.Layout, null);
        ws.FocusedPane = ws.Panes.FirstOrDefault();
        Rebuild(ws);
        foreach (var p in ws.Panes) _ = InitPane(p);
        Workspaces.Move(Workspaces.IndexOf(ws), Math.Clamp(item.Index, 0, Workspaces.Count - 1));
        Select(ws);
        return $"reopened {item.Label}";
    }

    public void MoveWorkspace(Workspace ws, int newIndex)
    {
        int i = Workspaces.IndexOf(ws);
        // Pinned workspaces stay above the others.
        int pinned = Workspaces.Count(w => w.Pinned);
        newIndex = ws.Pinned ? Math.Clamp(newIndex, 0, pinned - 1) : Math.Clamp(newIndex, pinned, Workspaces.Count - 1);
        if (i < 0 || i == newIndex) return;
        Workspaces.Move(i, newIndex);
        Window.SyncSelection(Selected ?? ws);
    }

    // ---- Tabs ----

    /// <summary>The shell for a new pane opened from `pane` (see TerminalPane.ShellForNewPane); null = the default.</summary>
    public static string? ShellFrom(PaneBase? pane) => (pane as TerminalPane)?.ShellForNewPane();

    /// <summary>Open a new terminal tab next to `near` (same folder; `shell`, else the same shell), or add `pane` as a tab.</summary>
    public PaneBase NewTab(PaneBase near, PaneBase? pane = null, string? shell = null)
    {
        pane ??= CreateTerminal(near.Workspace, near.Cwd, null, shell: shell ?? ShellFrom(near));
        near.Workspace.ZoomedPane = null;
        near.Stack.Add(pane, near.Stack.Tabs.IndexOf(near) + 1);
        _ = InitPane(pane);
        Focus(pane);
        Monitors?.Poke();
        return pane;
    }

    public void CycleTab(int delta)
    {
        var pane = Selected?.FocusedPane;
        if (pane == null || pane.Stack.Tabs.Count < 2) return;
        Focus(pane.Stack.Neighbor(delta));
    }

    // ---- Focus ----

    public void Focus(PaneBase pane)
    {
        var ws = pane.Workspace;
        if (Selected != ws)
        {
            ws.FocusedPane = pane;
            Select(ws);
            return;
        }
        if (pane.Stack.Active != pane) pane.Stack.Activate(pane);
        if (ws.ZoomedPane != null && ws.ZoomedPane.Stack == pane.Stack && ws.ZoomedPane != pane) ws.ZoomedPane = pane;
        SetFocused(pane);
        _focusTarget = pane;
        _focusGuardUntil = DateTime.UtcNow.AddMilliseconds(600);
        pane.FocusContent();
    }

    /// <summary>Called when the user clicks/types into a pane (focus already moved).</summary>
    public void OnPaneFocused(PaneBase pane)
    {
        if (pane.Workspace != Selected || pane.Stack?.Active != pane) return;
        if (pane != _focusTarget && DateTime.UtcNow < _focusGuardUntil) return;
        if (pane.Workspace.FocusedPane == pane && pane.IsFocused && !pane.Attention) return;
        SetFocused(pane);
    }

    private void SetFocused(PaneBase pane)
    {
        var ws = pane.Workspace;
        ws.FocusedPane = pane;
        foreach (var p in ws.Panes) p.IsFocused = p == pane;
        pane.Attention = false;
        Notifications.MarkRead(pane);
        ws.Refresh();
        Window.UpdateTitle();
    }

    /// <summary>Directional pane navigation (Ctrl+Alt+Arrow).</summary>
    public void MoveFocus(int dx, int dy)
    {
        var ws = Selected;
        var cur = ws?.FocusedPane;
        if (ws == null || cur == null) return;
        Unzoom(ws);
        ws.Container.UpdateLayout();
        Rect R(PaneStack s) => s.Host.TransformToAncestor(ws.Container).TransformBounds(new Rect(s.Host.RenderSize));
        var r0 = R(cur.Stack);
        PaneStack? best = null;
        double bestScore = double.MaxValue;
        foreach (var p in ws.Stacks)
        {
            if (p == cur.Stack) continue;
            var r = R(p);
            bool inDir = dx > 0 ? r.Left >= r0.Right - 6 : dx < 0 ? r.Right <= r0.Left + 6 : dy > 0 ? r.Top >= r0.Bottom - 6 : r.Bottom <= r0.Top + 6;
            if (!inDir) continue;
            double primary = dx != 0 ? Math.Abs((r.Left + r.Right) / 2 - (r0.Left + r0.Right) / 2) : Math.Abs((r.Top + r.Bottom) / 2 - (r0.Top + r0.Bottom) / 2);
            double secondary = dx != 0 ? Math.Abs((r.Top + r.Bottom) / 2 - (r0.Top + r0.Bottom) / 2) : Math.Abs((r.Left + r.Right) / 2 - (r0.Left + r0.Right) / 2);
            var score = primary + 2 * secondary;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (best != null) Focus(best.Active);
    }

    // ---- Layout ----

    public void Rebuild(Workspace ws)
    {
        ws.Container.Children.Clear();
        if (ws.ZoomedPane != null && ws.FindPane(ws.ZoomedPane.Id) == null) ws.ZoomedPane = null;
        if (ws.ZoomedPane is { } zoomed)
        {
            // Park the hidden stacks outside the tree; their shells and views keep running.
            foreach (var s in ws.Stacks) PaneStack.Detach(s.Host);
            ws.Container.Children.Add(zoomed.Stack.Host);
        }
        else if (ws.Root != null)
        {
            ws.Container.Children.Add(Build(ws.Root));
        }
        foreach (var s in ws.Stacks) s.Refresh();
    }

    // ---- Zoom / equalize / resize ----

    public void ToggleZoom(PaneBase? pane = null)
    {
        var ws = pane?.Workspace ?? Selected;
        if (ws == null) return;
        pane ??= ws.FocusedPane;
        ws.ZoomedPane = ws.ZoomedPane != null || pane == null || ws.StackCount < 2 ? null : pane;
        Rebuild(ws);
        if (pane != null) Focus(pane);
        ws.Refresh();
        Window.UpdateTitle();
    }

    private void Unzoom(Workspace ws)
    {
        if (ws.ZoomedPane == null) return;
        ws.ZoomedPane = null;
        Rebuild(ws);
        Window.UpdateTitle();
    }

    /// <summary>Give every pane along each split direction an equal share.</summary>
    public void Equalize(Workspace? ws = null)
    {
        ws ??= Selected;
        if (ws?.Root == null) return;
        Unzoom(ws);
        Equalize(ws.Root);
    }

    private static int Slots(Node n, bool horizontal) =>
        n is SplitNode s && s.Horizontal == horizontal ? Slots(s.A, horizontal) + Slots(s.B, horizontal) : 1;

    private static void Equalize(Node n)
    {
        if (n is not SplitNode s) return;
        int a = Slots(s.A, s.Horizontal), b = Slots(s.B, s.Horizontal);
        s.Ratio = (double)a / (a + b);
        s.ApplyRatio();
        Equalize(s.A);
        Equalize(s.B);
    }

    /// <summary>Move the nearest divider in the arrow's direction (Ctrl+Shift+Alt+Arrow).</summary>
    public void ResizeFocused(int dx, int dy, double step = 0.05)
    {
        var pane = Selected?.FocusedPane;
        if (pane == null) return;
        Unzoom(pane.Workspace);
        bool horizontal = dx != 0;
        Node child = pane.Stack;
        for (var s = pane.Stack.Parent; s != null; child = s, s = s.Parent)
        {
            if (s.Horizontal != horizontal) continue;
            int dir = horizontal ? dx : dy;
            s.Ratio = Math.Clamp(s.Ratio + dir * step, 0.1, 0.9);
            s.ApplyRatio();
            return;
        }
    }

    private UIElement Build(Node n)
    {
        if (n is PaneStack p)
        {
            PaneStack.Detach(p.Host);
            return p.Host;
        }
        var s = (SplitNode)n;
        var g = new Grid();
        s.View = g;
        var a = Build(s.A);
        var b = Build(s.B);
        var splitter = new GridSplitter
        {
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Divider"],
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        if (s.Horizontal)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Ratio, GridUnitType.Star), MinWidth = 120 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - s.Ratio, GridUnitType.Star), MinWidth = 120 });
            Grid.SetColumn(a, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(b, 2);
            splitter.Cursor = Cursors.SizeWE;
            splitter.DragCompleted += (_, _) =>
            {
                double w0 = g.ColumnDefinitions[0].ActualWidth, w2 = g.ColumnDefinitions[2].ActualWidth;
                if (w0 + w2 > 0) s.Ratio = w0 / (w0 + w2);
            };
        }
        else
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(s.Ratio, GridUnitType.Star), MinHeight = 80 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - s.Ratio, GridUnitType.Star), MinHeight = 80 });
            Grid.SetRow(a, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(b, 2);
            splitter.Cursor = Cursors.SizeNS;
            splitter.DragCompleted += (_, _) =>
            {
                double h0 = g.RowDefinitions[0].ActualHeight, h2 = g.RowDefinitions[2].ActualHeight;
                if (h0 + h2 > 0) s.Ratio = h0 / (h0 + h2);
            };
        }
        g.Children.Add(a);
        g.Children.Add(splitter);
        g.Children.Add(b);
        return g;
    }


    public void DisposeAll()
    {
        // Close every shell and wait (bounded) so none outlives Bellboard.
        var closing = Workspaces.SelectMany(w => w.Panes).OfType<TerminalPane>().Select(t => t.CloseShellAsync()).ToList();
        if (Shells != null) closing.Add(Shells.CloseAsync());
        try { Task.WaitAll(closing.ToArray(), TimeSpan.FromSeconds(4)); } catch { }
        Monitors?.Dispose();
        TerminalPool?.Dispose();
        Shells?.Dispose();
        foreach (var p in Workspaces.SelectMany(w => w.Panes).ToList()) p.Dispose();
        Notifications?.Dispose();
        Remote?.Dispose();
    }
}
