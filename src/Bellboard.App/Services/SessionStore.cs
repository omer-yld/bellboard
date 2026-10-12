using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Bellboard.Core;
using Bellboard.Panes;

namespace Bellboard.Services;

/// <summary>
/// %LOCALAPPDATA%\Bellboard\session.json — workspaces, split layout, cwd, scrollback, browser URLs and
/// Claude Code session ids. Saved on exit and every 30s, so it also survives a reboot or crash.
/// </summary>
public static class SessionStore
{
    private const int ScrollbackRows = 1500;
    public static string FilePath => Path.Combine(Log.Dir, "session.json");

    public static async Task SaveAsync(MuxController mux, Window window)
    {
        var workspaces = new JsonArray();
        foreach (var ws in mux.Workspaces.ToList())
        {
            if (ws.Root == null) continue;
            // Feeds (read-only panes another program writes to) aren't kept; a workspace of only feeds isn't either.
            if (await CaptureAsync(ws.Root) is not { } layout) continue;
            var kept = ws.Panes.Where(p => p is not TerminalPane { Feed: true }).ToList();
            workspaces.Add(new JsonObject
            {
                ["name"] = ws.CustomName,
                ["muted"] = ws.Muted ? true : null,
                ["pinned"] = ws.Pinned ? true : null,
                ["agentTask"] = ws.AgentTask is { } at ? new JsonObject
                {
                    ["task"] = at.Task, ["agent"] = at.Agent, ["repo"] = at.Repo, ["worktree"] = at.Worktree,
                    ["branch"] = at.Branch, ["base"] = at.Base, ["created"] = at.Created.ToString("o"),
                } : null,
                ["layout"] = LayoutJson.ToJson(layout),
                ["focused"] = ws.FocusedPane == null ? -1 : kept.IndexOf(ws.FocusedPane),
            });
        }
        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        var doc = new JsonObject
        {
            ["version"] = 1,
            ["savedAt"] = DateTime.Now.ToString("o"),
            ["selected"] = mux.Selected == null ? 0 : mux.Workspaces.IndexOf(mux.Selected),
            ["window"] = new JsonObject
            {
                ["x"] = bounds.X, ["y"] = bounds.Y, ["w"] = bounds.Width, ["h"] = bounds.Height,
                ["maximized"] = window.WindowState == WindowState.Maximized,
                ["sidebar"] = ((MainWindow)window).SidebarWidth,
                ["sidebarHidden"] = ((MainWindow)window).SidebarHidden,
            },
            ["workspaces"] = workspaces,
            ["doNotDisturb"] = mux.Notifications.DoNotDisturb,
            ["recentDirs"] = new JsonArray(mux.RecentDirs.Select(d => (JsonNode)d).ToArray()),
            ["recentSsh"] = new JsonArray(mux.RecentSsh.Select(t => (JsonNode)t).ToArray()),
            ["browserHistory"] = new JsonArray(mux.BrowserHistory.Take(40).Select(h => (JsonNode)new JsonObject { ["url"] = h.Url, ["title"] = h.Title }).ToArray()),
        };
        Directory.CreateDirectory(Log.Dir);
        var tmp = FilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Layout without scrollback, synchronously (nothing awaits when scrollback is off); null when it's all feeds.</summary>
    public static LayoutSpec? CaptureLayout(Node n) => CaptureAsync(n, scrollback: false).GetAwaiter().GetResult();

    public static LayoutSpec CapturePaneLayout(PaneBase pane) => CapturePane(pane, scrollback: false).GetAwaiter().GetResult();

    /// <summary>Snapshot a live layout as data (optionally with scrollback), leaving feeds out; null when nothing is left.</summary>
    public static async Task<LayoutSpec?> CaptureAsync(Node n, bool scrollback = true)
    {
        switch (n)
        {
            case SplitNode s:
                var a = await CaptureAsync(s.A, scrollback);
                var b = await CaptureAsync(s.B, scrollback);
                return a == null ? b : b == null ? a : new SplitSpec(s.Horizontal, s.Ratio, a, b);
            case PaneStack st:
                var kept = st.Tabs.Where(t => t is not TerminalPane { Feed: true }).ToList();
                if (kept.Count == 0) return null;
                if (kept.Count == 1) return await CapturePane(kept[0], scrollback);
                var tabs = new List<LayoutSpec>();
                foreach (var t in kept) tabs.Add(await CapturePane(t, scrollback));
                return new StackSpec(tabs, Math.Max(0, kept.IndexOf(st.Active)));
            default:
                throw new InvalidOperationException("unknown node");
        }
    }

    private static async Task<LayoutSpec> CapturePane(PaneBase pane, bool scrollback)
    {
        switch (pane)
        {
            case TerminalPane t:
                var agent = t.SessionAgent;
                return new TerminalSpec(
                    t.Cwd,
                    scrollback ? await t.SerializeAsync(ScrollbackRows) ?? t.RestoreScrollback : null,
                    agent?.SessionId != null ? agent.Kind : null,
                    agent?.SessionId,
                    Shell: t.Shell,
                    Pinned: t.Pinned);
            case BrowserPane b:
                return new BrowserSpec(b.Url, b.Pinned);
            default:
                throw new InvalidOperationException("unknown pane");
        }
    }

    public static JsonObject? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null;
        }
        catch (Exception ex)
        {
            Log.Error("session load failed", ex);
            return null;
        }
    }

    public static void ApplyWindow(JsonObject session, MainWindow window)
    {
        if (session["window"] is not JsonObject w) return;
        double x = (double?)w["x"] ?? double.NaN, y = (double?)w["y"] ?? double.NaN;
        double width = (double?)w["w"] ?? 0, height = (double?)w["h"] ?? 0;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (width >= 400 && height >= 300 && vs.IntersectsWith(new Rect(x, y, width, height)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = x;
            window.Top = y;
            window.Width = width;
            window.Height = height;
        }
        if ((bool?)w["maximized"] == true) window.WindowState = WindowState.Maximized;
        if ((double?)w["sidebar"] is { } sb && sb >= 140 && sb <= 600) window.SidebarWidth = sb;
        if ((bool?)w["sidebarHidden"] == true) window.SidebarHidden = true;
    }

    /// <summary>Rebuilds workspaces from a saved session. Returns false if nothing was restored.</summary>
    public static bool Restore(MuxController mux, JsonObject session)
    {
        foreach (var t in (session["recentSsh"] as JsonArray ?? new JsonArray()).Select(n => (string?)n).Where(t => t != null).Reverse())
            mux.RememberSsh(t!);
        foreach (var d in (session["recentDirs"] as JsonArray ?? new JsonArray()).Select(n => (string?)n).Where(d => d != null).Reverse())
            mux.RememberDirectory(d);
        foreach (var h in (session["browserHistory"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Reverse())
            if ((string?)h["url"] is { } url) mux.RememberPage(url, (string?)h["title"]);
        if (session["workspaces"] is not JsonArray list || list.Count == 0) return false;
        foreach (var item in list.OfType<JsonObject>())
        {
            try
            {
                var ws = mux.AddWorkspace((string?)item["name"]);
                ws.Muted = (bool?)item["muted"] == true;
                ws.Pinned = (bool?)item["pinned"] == true;
                if (item["agentTask"] is JsonObject at && (string?)at["worktree"] is { } wt && (string?)at["branch"] is { } br)
                    ws.AgentTask = new AgentTask((string?)at["task"] ?? "", (string?)at["agent"] ?? "claude", (string?)at["repo"] ?? "",
                        wt, br, (string?)at["base"] ?? "main", DateTime.TryParse((string?)at["created"], out var c) ? c : DateTime.Now);
                ws.Root = Build(mux, ws, LayoutJson.FromJson(item["layout"] as JsonObject), null);
                // An agent workspace whose agent never reported a session (no hooks, Codex/OpenCode):
                // continue the latest conversation in its worktree.
                if (ws.AgentTask is { } task && ws.Panes.OfType<TerminalPane>().All(t => t.RestoreAgent == null)
                    && ws.Panes.OfType<TerminalPane>().FirstOrDefault() is { } first && AgentManager.ResumeCommand(task.Agent, null) != null)
                    first.RestoreAgent = new AgentInfo(task.Agent, null, "idle", DateTime.Now);
                var panes = ws.Panes.ToList();
                int f = (int?)item["focused"] ?? 0;
                ws.FocusedPane = f >= 0 && f < panes.Count ? panes[f] : panes.FirstOrDefault();
                mux.Rebuild(ws);
                foreach (var p in panes) _ = mux.InitPane(p);
            }
            catch (Exception ex)
            {
                Log.Error("restoring a workspace failed", ex);
            }
        }
        if ((bool?)session["doNotDisturb"] == true) mux.Notifications.DoNotDisturb = true;
        if (mux.Workspaces.Count == 0) return false;
        int sel = (int?)session["selected"] ?? 0;
        mux.Select(mux.Workspaces[Math.Clamp(sel, 0, mux.Workspaces.Count - 1)]);
        mux.Notifications.UpdateCounts();
        Log.Info($"restored {mux.Workspaces.Count} workspace(s) from {session["savedAt"]}");
        return true;
    }

    /// <summary>Create (uninitialized) panes for a layout spec.</summary>
    public static Node Build(MuxController mux, Workspace ws, LayoutSpec spec, SplitNode? parent)
    {
        switch (spec)
        {
            case SplitSpec sp:
                var s = new SplitNode { Horizontal = sp.Horizontal, Ratio = sp.Ratio, Parent = parent };
                s.A = Build(mux, ws, sp.A, s);
                s.B = Build(mux, ws, sp.B, s);
                return s;
            case StackSpec st:
            {
                var panes = st.Tabs.Select(t => BuildPane(mux, ws, t)).ToList();
                var stack = new PaneStack(mux, panes[0]) { Parent = parent };
                foreach (var extra in panes.Skip(1)) stack.Add(extra);
                stack.Activate(panes[Math.Clamp(st.Active, 0, panes.Count - 1)]);
                return stack;
            }
            default:
                return new PaneStack(mux, BuildPane(mux, ws, spec)) { Parent = parent };
        }
    }

    private static PaneBase BuildPane(MuxController mux, Workspace ws, LayoutSpec spec)
    {
        switch (spec)
        {
            case BrowserSpec b:
                var browser = mux.CreateBrowser(ws, UrlTools.MigrateLegacy(b.Url, WebEnv.HostName));
                browser.Pinned = b.Pinned;
                return browser;
            case TerminalSpec t:
                var agent = t.AgentSessionId != null ? new AgentInfo(t.AgentKind ?? "claude", t.AgentSessionId, "idle", DateTime.Now) : null;
                var terminal = mux.CreateTerminal(ws, t.Cwd, t.Command, t.Scrollback, agent, t.Shell);
                terminal.Pinned = t.Pinned;
                return terminal;
            default:
                throw new InvalidOperationException("unknown pane spec");
        }
    }
}
