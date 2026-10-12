using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Bellboard.Core;
using Bellboard.Panes;
using Bellboard.Services;
using Bellboard.Shared;

namespace Bellboard.Ipc;

/// <summary>Every action in Bellboard is reachable here, so agents and scripts can drive it like a user.</summary>
public sealed class CommandRouter
{
    private readonly MuxController _mux;

    public CommandRouter(MuxController mux) => _mux = mux;

    public async Task<JsonNode?> HandleAsync(string method, JsonObject p)
    {
        switch (method)
        {
            // ---- app ----
            case "system.ping":
                return new JsonObject
                {
                    ["version"] = BellboardProtocol.Version,
                    ["pid"] = Environment.ProcessId,
                    ["webview2"] = WebEnv.BrowserVersion,
                    ["conpty"] = Bellboard.Terminal.PtySession.BundledAvailable ? "bundled" : "inbox",
                    ["workspaces"] = _mux.Workspaces.Count,
                    ["toasts"] = _mux.Notifications.ToastStatus,
                };
            case "app.activate":
                _mux.Window.ActivateFromTray();
                return true;
            case "app.quit":
                _ = _mux.Window.Dispatcher.InvokeAsync(() =>
                {
                    // An open dialog (settings, new agent…) would block closing the main window.
                    foreach (var owned in _mux.Window.OwnedWindows.OfType<Window>().ToList()) owned.Close();
                    _mux.Window.Close();
                });
                return true;
            case "app.screenshot":
                // "screen": copy the window's area from the screen, which includes open menus/popups.
                return new JsonObject
                {
                    ["png"] = Convert.ToBase64String(Bool(p, "screen", false)
                        ? Win32.CaptureScreenAreaPng(_mux.Window.Handle)
                        : Win32.CaptureWindowPng(_mux.Window.Handle)),
                };
            case "dev.menu" when App.DevMode:
                _mux.Window.OpenMenuForTest(Str(p, "which") ?? "workspace");
                return true;
            case "dev.drag" when App.DevMode:
            {
                // A pane drag without the mouse: begin (pane), move (x, y in window coordinates), drop, cancel.
                var drag = _mux.Window.Drag;
                switch (Str(p, "step"))
                {
                    case "begin": drag.Begin(Pane(p), simulated: true); break;
                    case "move": drag.MoveTo(new Point(double.Parse(Str(p, "x") ?? "0", System.Globalization.CultureInfo.InvariantCulture), double.Parse(Str(p, "y") ?? "0", System.Globalization.CultureInfo.InvariantCulture))); break;
                    case "drop": drag.Finish(drop: true); break;
                    default: drag.Finish(drop: false); break;
                }
                return new JsonObject { ["dragging"] = drag.IsDragging, ["target"] = drag.TargetText };
            }

            // ---- workspaces ----
            case "workspace.list":
                return ListWorkspaces();
            case "workspace.new":
            {
                var cwd = Str(p, "cwd");
                if (cwd != null && !System.IO.Directory.Exists(cwd)) throw new InvalidOperationException($"directory not found: {cwd}");
                var ws = _mux.NewWorkspace(Str(p, "name"), cwd ?? _mux.DefaultNewWorkspaceDir(), Str(p, "command"), Bool(p, "focus", true), Str(p, "shell"));
                return new JsonObject { ["workspace"] = ws.Id, ["pane"] = ws.FocusedPane?.Id };
            }
            case "workspace.select":
            {
                var ws = Workspace(p);
                _mux.Select(ws);
                return new JsonObject { ["workspace"] = ws.Id };
            }
            case "workspace.rename":
            {
                var ws = Workspace(p);
                ws.CustomName = string.IsNullOrWhiteSpace(Str(p, "name")) ? null : Str(p, "name");
                ws.Refresh();
                _mux.Window.UpdateTitle();
                return new JsonObject { ["workspace"] = ws.Id, ["name"] = ws.Name };
            }
            case "workspace.close":
            {
                var ws = Workspace(p);
                if (ws.Pinned) throw new InvalidOperationException($"{ws.Id} is pinned; unpin it first (bell unpin workspace --workspace {ws.Id})");
                _mux.CloseWorkspace(ws);
                return true;
            }
            case "workspace.pin":
            {
                var ws = Workspace(p);
                _mux.SetPinned(ws, Bool(p, "on", !ws.Pinned));
                return new JsonObject { ["workspace"] = ws.Id, ["pinned"] = ws.Pinned, ["index"] = _mux.Workspaces.IndexOf(ws) + 1 };
            }
            case "workspace.move":
            {
                var ws = Workspace(p);
                var to = Str(p, "to") ?? "";
                int index = to switch
                {
                    "up" => _mux.Workspaces.IndexOf(ws) - 1,
                    "down" => _mux.Workspaces.IndexOf(ws) + 1,
                    "top" => 0,
                    "bottom" => _mux.Workspaces.Count - 1,
                    _ when int.TryParse(to, out var n) => n - 1,
                    _ => throw new InvalidOperationException("to must be up|down|top|bottom|<position>"),
                };
                _mux.MoveWorkspace(ws, index);
                return new JsonObject { ["workspace"] = ws.Id, ["index"] = _mux.Workspaces.IndexOf(ws) + 1 };
            }
            case "app.reopen":
                return new JsonObject { ["result"] = _mux.ReopenClosed() ?? "nothing to reopen" };
            case "workspace.next":
                _mux.SelectRelative(Int(p, "delta", 1));
                return new JsonObject { ["workspace"] = _mux.Selected?.Id };

            // ---- panes ----
            case "pane.split":
            {
                var target = Pane(p);
                bool horizontal = (Str(p, "direction") ?? "right") is "right" or "left" or "h" or "horizontal";
                PaneBase created;
                if (Str(p, "kind") == "browser")
                {
                    created = _mux.CreateBrowser(target.Workspace, Str(p, "url"));
                    _mux.Split(target, horizontal, created);
                }
                else
                {
                    created = _mux.SplitTerminal(target, horizontal, Str(p, "cwd"), Str(p, "command"), Str(p, "shell"));
                }
                if (!Bool(p, "focus", true)) _mux.Focus(target);
                return new JsonObject { ["pane"] = created.Id, ["workspace"] = created.Workspace.Id };
            }
            case "pane.close":
            {
                var pane = Pane(p);
                if (pane.Pinned)
                    throw new InvalidOperationException($"{pane.Id} is pinned; unpin it first (bell unpin tab --pane {pane.Id})");
                if (MuxController.PinnedReason(pane) != null)
                    throw new InvalidOperationException($"{pane.Id} is the last pane of pinned workspace {pane.Workspace.Id}; unpin it first (bell unpin workspace --workspace {pane.Workspace.Id})");
                _mux.ClosePane(pane);
                return true;
            }
            case "pane.pin":
            {
                var pane = Pane(p);
                _mux.SetPinned(pane, Bool(p, "on", !pane.Pinned));
                return new JsonObject { ["pane"] = pane.Id, ["pinned"] = pane.Pinned };
            }
            case "pane.focus":
            {
                var pane = Pane(p);
                _mux.Window.ActivateFromTray();
                _mux.Focus(pane);
                return new JsonObject { ["pane"] = pane.Id };
            }
            case "pane.move-focus":
                _mux.MoveFocus(Int(p, "dx", 0), Int(p, "dy", 0));
                return new JsonObject { ["pane"] = _mux.Selected?.FocusedPane?.Id };
            case "tab.new":
            {
                var near = Pane(p);
                PaneBase tab = Str(p, "kind") == "browser" || Str(p, "url") != null
                    ? _mux.CreateBrowser(near.Workspace, Str(p, "url"))
                    : _mux.CreateTerminal(near.Workspace, Str(p, "cwd") ?? near.Cwd, Str(p, "command"), shell: Str(p, "shell") ?? MuxController.ShellFrom(near));
                _mux.NewTab(near, tab);
                if (!Bool(p, "focus", true)) _mux.Focus(near);
                return new JsonObject { ["pane"] = tab.Id, ["tabs"] = tab.Stack.Tabs.Count };
            }
            case "tab.next":
                if (Str(p, "pane") != null || Str(p, "caller") != null) _mux.Focus(Pane(p));
                _mux.CycleTab(Int(p, "delta", 1));
                return new JsonObject { ["pane"] = _mux.Selected?.FocusedPane?.Id };
            case "pane.zoom":
            {
                var pane = Pane(p);
                bool want = Bool(p, "on", pane.Workspace.ZoomedPane != pane);
                if ((pane.Workspace.ZoomedPane == pane) != want) _mux.ToggleZoom(pane);
                return new JsonObject { ["pane"] = pane.Id, ["zoomed"] = pane.Workspace.ZoomedPane == pane };
            }
            case "layout.equalize":
                _mux.Equalize(Workspace(p));
                return true;
            case "pane.move":
            {
                var pane = Pane(p);
                if (MuxController.StayReason(pane) is { } stays && (Str(p, "into") != null || _mux.FindPane(Str(p, "to"))?.Workspace != pane.Workspace))
                    throw new InvalidOperationException(stays.ToLowerInvariant());
                if (Str(p, "into") is { } into)
                {
                    var dest = into == "new" ? null : _mux.FindWorkspace(into) ?? throw new InvalidOperationException($"no workspace '{into}'");
                    if (dest == pane.Workspace) throw new InvalidOperationException($"{pane.Id} is already in {dest.Id}");
                    var moved = _mux.MoveToWorkspace(pane, dest) ?? throw new InvalidOperationException($"{pane.Id} is already alone in its workspace");
                    return new JsonObject { ["pane"] = pane.Id, ["workspace"] = moved.Id };
                }
                var to = Str(p, "to") ?? throw new InvalidOperationException("say where: a target pane (to) or a workspace (into)");
                var target = _mux.FindPane(to) ?? throw new InvalidOperationException($"no pane '{to}' (see `bell list`)");
                var side = (Str(p, "side") ?? "tab").ToLowerInvariant() switch
                {
                    "left" => DropSide.Left,
                    "right" => DropSide.Right,
                    "up" or "top" or "above" => DropSide.Up,
                    "down" or "bottom" or "below" => DropSide.Down,
                    "tab" => DropSide.Tab,
                    var d => throw new InvalidOperationException($"side must be left|right|up|down|tab, not '{d}'"),
                };
                int? index = p["index"] != null ? Int(p, "index", 0) : null;
                bool changed = _mux.MovePane(pane, target, side, index);
                return new JsonObject { ["pane"] = pane.Id, ["workspace"] = pane.Workspace.Id, ["moved"] = changed };
            }
            case "layout.flip" or "layout.swap":
            {
                var pane = Pane(p);
                bool done = method == "layout.flip" ? _mux.FlipSplit(pane) : _mux.SwapSides(pane);
                if (!done) throw new InvalidOperationException($"{pane.Id} isn't in a split");
                var split = pane.Stack.Parent!;
                return new JsonObject { ["pane"] = pane.Id, ["layout"] = split.Horizontal ? "side by side" : "top and bottom" };
            }
            case "pane.resize":
            {
                var pane = Pane(p);
                _mux.Focus(pane);
                var (dx, dy) = (Str(p, "direction") ?? "right") switch
                {
                    "left" => (-1, 0), "right" => (1, 0), "up" => (0, -1), "down" => (0, 1),
                    var d => throw new InvalidOperationException($"direction must be left|right|up|down, not '{d}'"),
                };
                _mux.ResizeFocused(dx, dy, Math.Clamp(Int(p, "percent", 5), 1, 50) / 100.0);
                return new JsonObject { ["pane"] = pane.Id };
            }
            case "terminal.find":
                Terminal(p).OpenFind();
                return true;
            case "shell.list":
                return new JsonArray(Bellboard.Terminal.ShellResolver.Available(includeSsh: true).Select(s => (JsonNode)new JsonObject { ["alias"] = s.Alias, ["label"] = s.Label }).ToArray());
            case "pane.screenshot":
                return new JsonObject { ["png"] = Convert.ToBase64String(await Pane(p).CapturePngAsync()) };

            // ---- terminals ----
            case "terminal.send":
            {
                var t = Terminal(p);
                var text = Str(p, "text") ?? "";
                if (Bool(p, "enter", false)) text += "\r";
                t.SendText(text);
                return new JsonObject { ["pane"] = t.Id, ["bytes"] = text.Length };
            }
            case "terminal.send-key":
            {
                var t = Terminal(p);
                var keys = (p["keys"] as JsonArray)?.Select(k => (string?)k ?? "").ToList() ?? new List<string>();
                if (keys.Count == 0) throw new InvalidOperationException("no keys given (e.g. enter, ctrl+c, shift+tab, up)");
                string seq;
                try { seq = KeySequences.ToSequence(keys); }
                catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message); }
                t.SendText(seq);
                return new JsonObject { ["pane"] = t.Id, ["keys"] = keys.Count };
            }
            case "terminal.read":
            {
                var t = Terminal(p);
                return new JsonObject { ["pane"] = t.Id, ["text"] = await t.ReadAsync(Int(p, "lines", 0)) };
            }

            // ---- notifications ----
            case "notify":
            {
                var pane = Pane(p);
                var body = Str(p, "body") ?? "";
                var n = _mux.Notifications.Post(pane, Str(p, "title"), body, Str(p, "source") ?? "cli");
                return new JsonObject { ["notification"] = n.Id, ["pane"] = pane.Id };
            }
            case "notification.list":
                return new JsonArray(_mux.Notifications.Items.Select(n => (JsonNode)new JsonObject
                {
                    ["id"] = n.Id,
                    ["at"] = n.At.ToString("o"),
                    ["workspace"] = n.Workspace.Id,
                    ["pane"] = n.Pane.Id,
                    ["title"] = n.Title,
                    ["body"] = n.Body,
                    ["source"] = n.Source,
                    ["read"] = n.Read,
                }).ToArray());
            case "notification.clear":
                _mux.Notifications.MarkAllRead();
                return true;
            case "notification.jump":
                _mux.Notifications.JumpToLatestUnread();
                return true;
            case "notification.dnd":
                _mux.Notifications.DoNotDisturb = Bool(p, "on", !_mux.Notifications.DoNotDisturb);
                return new JsonObject { ["doNotDisturb"] = _mux.Notifications.DoNotDisturb };
            case "workspace.broadcast":
            {
                var ws = Workspace(p);
                _mux.SetBroadcast(ws, Bool(p, "on", !ws.Broadcast));
                return new JsonObject { ["workspace"] = ws.Id, ["broadcast"] = ws.Broadcast };
            }
            case "workspace.mute":
            {
                var ws = Workspace(p);
                _mux.Window.SetMuted(ws, Bool(p, "on", !ws.Muted));
                return new JsonObject { ["workspace"] = ws.Id, ["muted"] = ws.Muted };
            }
            case "app.palette":
                _mux.Window.ActivateFromTray();
                _mux.Window.OpenPalette(Str(p, "query") ?? "");
                return true;
            case "dev.unload" when App.DevMode:
                await _mux.UnloadIdleAsync(force: true, only: Workspace(p));
                return new JsonObject { ["unloaded"] = Workspace(p).Panes.OfType<TerminalPane>().Count(t => t.Unloaded) };
            case "dev.lowmem" when App.DevMode:
            {
                var ws = Workspace(p);
                bool low = Bool(p, "on", true);
                int n = 0;
                foreach (var pane in ws.Panes)
                {
                    if (pane.Web.CoreWebView2 is not { } core) continue;
                    core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
                    if (low && Bool(p, "suspend", false)) { try { await core.TrySuspendAsync(); } catch { } }
                    if (!low && core.IsSuspended) core.Resume();
                    n++;
                }
                return new JsonObject { ["panes"] = n, ["low"] = low };
            }
            case "dev.toasts" when App.DevMode:
                return new JsonArray(_mux.Notifications.ToastHistory().Select(t => (JsonNode)t).ToArray());
            case "dev.toast-activate" when App.DevMode:
                _mux.Notifications.OnToastActivated(Str(p, "argument") ?? "");
                return true;

            // ---- sidebar status API (cmux-compatible shape) ----
            case "status.set":
            {
                var ws = Workspace(p);
                var key = Str(p, "key") ?? throw new InvalidOperationException("key is required");
                var pill = new StatusPill(key, Str(p, "text") ?? "", Str(p, "color"));
                var i = ws.Status.FindIndex(s => s.Key == key);
                if (i >= 0) ws.Status[i] = pill; else ws.Status.Add(pill);
                ws.Refresh();
                return new JsonObject { ["workspace"] = ws.Id, ["key"] = key };
            }
            case "status.clear":
            {
                var ws = Workspace(p);
                if (Str(p, "key") is { } key) ws.Status.RemoveAll(s => s.Key == key); else ws.Status.Clear();
                ws.Refresh();
                return true;
            }
            case "status.list":
                return StatusJson(Workspace(p));
            case "progress.set":
            {
                var ws = Workspace(p);
                var v = Dbl(p, "value") ?? throw new InvalidOperationException("value is required (0..1 or 40%)");
                ws.ProgressValue = Math.Clamp(v > 1 ? v / 100 : v, 0, 1);
                ws.ProgressLabel = Str(p, "label");
                ws.Refresh();
                return new JsonObject { ["workspace"] = ws.Id, ["value"] = ws.ProgressValue };
            }
            case "progress.clear":
            {
                var ws = Workspace(p);
                ws.ProgressValue = null;
                ws.ProgressLabel = null;
                ws.Refresh();
                return true;
            }
            case "log.add":
            {
                var ws = Workspace(p);
                var level = (Str(p, "level") ?? "info").ToLowerInvariant();
                if (level is not ("info" or "progress" or "success" or "warning" or "error")) throw new InvalidOperationException("level must be info|progress|success|warning|error");
                ws.Logs.Add(new LogEntry(DateTime.Now, level, Str(p, "text") ?? ""));
                if (ws.Logs.Count > 200) ws.Logs.RemoveRange(0, ws.Logs.Count - 200);
                ws.Refresh();
                return new JsonObject { ["workspace"] = ws.Id, ["count"] = ws.Logs.Count };
            }
            case "log.clear":
            {
                var ws = Workspace(p);
                ws.Logs.Clear();
                ws.Refresh();
                return true;
            }
            case "log.list":
            {
                var ws = Workspace(p);
                return new JsonArray(ws.Logs.Skip(Math.Max(0, ws.Logs.Count - Int(p, "limit", 50)))
                    .Select(l => (JsonNode)new JsonObject { ["at"] = l.At.ToString("o"), ["level"] = l.Level, ["text"] = l.Text }).ToArray());
            }
            case "sidebar.state":
            {
                var ws = Workspace(p);
                var o = StatusJson(ws);
                o["workspace"] = ws.Id;
                o["name"] = ws.Name;
                o["cwd"] = ws.Cwd;
                o["branch"] = ws.Branch;
                o["ports"] = JsonSerializer.SerializeToNode(ws.Ports);
                o["unread"] = ws.Unread;
                o["notice"] = ws.LastNotice;
                o["agent"] = ws.HasAgent ? ws.AgentText : null;
                return o;
            }

            // ---- agents in worktrees ----
            case "agent.new":
            {
                var ws = await _mux.Agents.NewAsync(Str(p, "task") ?? "", Str(p, "agent"), Str(p, "repo"), Str(p, "base"),
                    Str(p, "name"), Str(p, "shell"), Bool(p, "focus", true), from: Str(p, "repo") == null ? Pane(p) as TerminalPane : null,
                    branchName: Str(p, "branch"));
                var t = ws.AgentTask!;
                return new JsonObject { ["workspace"] = ws.Id, ["pane"] = ws.FocusedPane?.Id, ["branch"] = t.Branch, ["worktree"] = t.Worktree, ["base"] = t.Base };
            }
            case "agent.list":
                return new JsonArray(_mux.Workspaces.Where(w => w.IsAgent).Select(w =>
                {
                    var t = w.AgentTask!;
                    var live = w.Panes.OfType<TerminalPane>().Select(x => x.Agent).FirstOrDefault(a => a != null);
                    return (JsonNode)new JsonObject
                    {
                        ["workspace"] = w.Id, ["name"] = w.Name, ["task"] = t.Task, ["agent"] = t.Agent,
                        ["branch"] = t.Branch, ["base"] = t.Base, ["worktree"] = t.Worktree,
                        ["status"] = live?.Status, ["unread"] = w.Unread, ["created"] = t.Created.ToString("o"),
                    };
                }).ToArray());
            case "agent.inbox":
                return new JsonArray(Inbox.Build(_mux).Select(r => (JsonNode)new JsonObject
                {
                    ["pane"] = r.Pane.Id, ["workspace"] = r.Pane.Workspace.Id, ["title"] = r.Title, ["status"] = r.Status,
                    ["since"] = r.Since == default ? null : r.Since.ToString("o"), ["message"] = r.Message, ["where"] = r.Where,
                    ["feed"] = r.Pane is TerminalPane { Feed: true },
                    ["agent"] = r.Pane is TerminalPane { Agent: { } running } ? AgentManager.DisplayName(running.Kind)
                        : r.Pane.Workspace.AgentTask is { } task ? AgentManager.DisplayName(task.Agent) : null,
                    ["workspaceName"] = r.Pane.Workspace.Name,
                }).ToArray());
            case "agent.info":
            {
                var ws = Workspace(p);
                var t = ws.AgentTask ?? throw new InvalidOperationException($"{ws.Name} isn't an agent workspace");
                var s = await _mux.Agents.InspectAsync(t);
                return new JsonObject
                {
                    ["workspace"] = ws.Id, ["task"] = t.Task, ["branch"] = t.Branch, ["base"] = t.Base, ["worktree"] = t.Worktree,
                    ["ahead"] = s.Ahead, ["diffstat"] = s.DiffStat, ["uncommitted"] = new JsonArray(s.Dirty.Select(d => (JsonNode)d).ToArray()),
                    ["repoBranch"] = s.RepoBranch, ["repoDirty"] = s.RepoDirty, ["gh"] = s.GhAvailable, ["origin"] = s.HasRemote,
                };
            }
            case "agent.finish":
            {
                var ws = Workspace(p);
                var message = await _mux.Agents.FinishAsync(ws, Str(p, "mode") ?? "", Bool(p, "force", false));
                return new JsonObject { ["result"] = message };
            }

            // ---- feeds: read-only panes another program reports into (AI Buddy's coding-agent runs) ----
            case "feed.open":
            {
                var name = Str(p, "name") ?? "Feed";
                var pane = _mux.NewFeed(name, Str(p, "cwd"), Str(p, "badge") is { } badge ? badge[..Math.Min(4, badge.Length)].ToUpperInvariant() : null,
                    Str(p, "label"), Bool(p, "focus", false));
                return new JsonObject { ["pane"] = pane.Id, ["workspace"] = pane.Workspace.Id };
            }
            case "feed.write":
            {
                var feed = Feed(p);
                feed.WriteFeed(Str(p, "text") ?? "");
                return new JsonObject { ["pane"] = feed.Id };
            }
            case "feed.status":
            {
                // The sidebar's agent line and the inbox: working / waiting / done, with a short message. No toast:
                // the program that owns the feed tells you itself.
                var feed = Feed(p);
                var status = Str(p, "status") ?? "working";
                if (status is not ("working" or "waiting" or "done" or "idle"))
                    throw new InvalidOperationException("status must be working, waiting, done or idle");
                feed.Agent = new AgentInfo(Str(p, "kind") ?? feed.Agent?.Kind ?? "feed", null, status,
                    status == feed.Agent?.Status ? feed.Agent.Since : DateTime.Now, Clip(Str(p, "message")));
                feed.Workspace.Refresh();
                _mux.Window.UpdateTitle();
                return new JsonObject { ["pane"] = feed.Id, ["status"] = status };
            }

            // ---- agent hooks ----
            case "agent.hook":
                return AgentHook(p);

            // ---- browser ----
            case "browser.open":
            {
                var url = Str(p, "url");
                var b = Browser(p, create: true, url);
                if (url != null && b.Url != BrowserPane.NormalizeUrl(url)) await b.NavigateAndWaitAsync(url, Int(p, "timeout", 15000));
                else await b.WaitLoadedAsync(Int(p, "timeout", 15000));
                return BrowserInfo(b);
            }
            case "browser.navigate":
            {
                var b = Browser(p);
                var ok = await b.NavigateAndWaitAsync(Str(p, "url") ?? throw new InvalidOperationException("url is required"), Int(p, "timeout", 15000));
                var info = BrowserInfo(b);
                info["loaded"] = ok;
                return info;
            }
            case "browser.back":
            case "browser.forward":
            case "browser.reload":
            {
                var b = Browser(p);
                await b.GoAsync(method["browser.".Length..], Int(p, "timeout", 15000));
                return BrowserInfo(b);
            }
            case "browser.url":
            {
                var b = Browser(p);
                await b.WaitLoadedAsync(Int(p, "timeout", 15000));
                return BrowserInfo(b);
            }
            case "browser.snapshot":
            {
                var b = Browser(p);
                await b.WaitLoadedAsync(Int(p, "timeout", 15000));
                return new JsonObject { ["pane"] = b.Id, ["snapshot"] = await b.SnapshotAsync() };
            }
            case "browser.click":
            {
                var b = Browser(p);
                await b.ClickAsync(Str(p, "target") ?? throw new InvalidOperationException("target is required"));
                return BrowserInfo(b);
            }
            case "browser.type":
            {
                var b = Browser(p);
                await b.TypeAsync(Str(p, "target"), Str(p, "text") ?? "", Bool(p, "clear", false), Bool(p, "submit", false));
                return BrowserInfo(b);
            }
            case "browser.press":
            {
                var b = Browser(p);
                await b.PressAsync(Str(p, "key") ?? throw new InvalidOperationException("key is required"));
                return BrowserInfo(b);
            }
            case "browser.eval":
            {
                var b = Browser(p);
                return new JsonObject { ["value"] = await b.EvalAsync(Str(p, "js") ?? throw new InvalidOperationException("js is required")) };
            }
            case "browser.wait":
            {
                var b = Browser(p);
                var ok = await b.WaitAsync(Str(p, "selector"), Str(p, "text"), Int(p, "timeout", 10000));
                if (!ok) throw new InvalidOperationException("timed out waiting");
                return BrowserInfo(b);
            }
            case "browser.console":
                return Browser(p).ConsoleLog(Int(p, "last", 100), Bool(p, "clear", false));
            case "browser.network":
                return Browser(p).NetworkLog(Int(p, "last", 100), Bool(p, "clear", false));
            case "browser.screenshot":
            {
                var b = Browser(p);
                return new JsonObject { ["png"] = await b.ScreenshotBase64Async(Bool(p, "full", false)) };
            }

            default:
                throw new InvalidOperationException($"unknown method '{method}'");
        }
    }

    private JsonObject ListWorkspaces()
    {
        var arr = new JsonArray();
        int i = 0;
        foreach (var ws in _mux.Workspaces)
        {
            i++;
            arr.Add(new JsonObject
            {
                ["id"] = ws.Id,
                ["index"] = i,
                ["name"] = ws.Name,
                ["selected"] = ws == _mux.Selected,
                ["cwd"] = ws.Cwd,
                ["branch"] = ws.Branch,
                ["ports"] = JsonSerializer.SerializeToNode(ws.Ports),
                ["unread"] = ws.Unread,
                ["notice"] = ws.LastNotice,
                ["muted"] = ws.Muted,
                ["pinned"] = ws.Pinned,
                ["status"] = ws.HasAgent ? ws.AgentText : null,
                ["panes"] = new JsonArray(ws.Panes.Select(pn => JsonSerializer.SerializeToNode(pn.Describe())).ToArray()),
            });
        }
        return new JsonObject { ["selected"] = _mux.Selected?.Id, ["workspaces"] = arr };
    }

    private JsonNode? AgentHook(JsonObject p)
    {
        if (_mux.FindPane(Str(p, "pane")) is not TerminalPane pane) return new JsonObject { ["ignored"] = true };
        var agent = Str(p, "agent") ?? "claude";
        var ev = Str(p, "event") ?? "";
        var payload = p["payload"] as JsonObject;
        var sid = (string?)payload?["session_id"] ?? pane.Agent?.SessionId ?? pane.RestoreAgent?.SessionId;
        var title = pane.DisplayTitle;

        if (agent == "claude")
        {
            switch (ev)
            {
                case "SessionStart":
                    pane.Agent = new AgentInfo("claude", sid, "idle", DateTime.Now, "Session started");
                    break;
                case "UserPromptSubmit":
                    pane.Agent = new AgentInfo("claude", sid, "working", DateTime.Now, Clip((string?)payload?["prompt"]));
                    pane.Attention = false;
                    break;
                case "Notification":
                    var ask = (string?)payload?["message"] ?? "Claude needs your attention";
                    pane.Agent = new AgentInfo("claude", sid, "waiting", DateTime.Now, ask);
                    _mux.Notifications.Post(pane, "Claude Code", ask, "claude");
                    break;
                case "Stop":
                    var done = title != null ? $"Finished: {title.TrimStart('✳', '*', ' ')}" : "Finished — ready for your next prompt";
                    pane.Agent = new AgentInfo("claude", sid, "done", DateTime.Now, done);
                    _mux.Notifications.Post(pane, "Claude Code", done, "claude");
                    break;
                case "SessionEnd":
                    pane.Agent = null;
                    pane.Subagents.Clear();
                    break;
                case "SubagentStart":
                    if ((string?)payload?["agent_id"] is { } startId)
                        pane.Subagents[startId] = ((string?)payload?["agent_type"] ?? "subagent", DateTime.Now);
                    break;
                case "SubagentStop":
                {
                    var stopId = (string?)payload?["agent_id"];
                    var type = (string?)payload?["agent_type"] ?? (stopId != null && pane.Subagents.TryGetValue(stopId, out var s) ? s.Type : "subagent");
                    if (stopId != null) pane.Subagents.Remove(stopId);
                    var said = Clip((string?)payload?["last_assistant_message"]);
                    pane.Workspace.Logs.Add(new LogEntry(DateTime.Now, "success", $"{type} finished{(said != null ? ": " + said : "")}"));
                    break;
                }
                case "TeammateIdle":
                    pane.Workspace.Logs.Add(new LogEntry(DateTime.Now, "info", $"teammate {(string?)payload?["teammate_name"] ?? "?"} is idle"));
                    break;
                case "TaskCompleted":
                    pane.Workspace.Logs.Add(new LogEntry(DateTime.Now, "success",
                        $"task done: {(string?)payload?["task_subject"] ?? "task"}{((string?)payload?["teammate_name"] is { } who ? " (" + who + ")" : "")}"));
                    break;
            }
        }
        else if (agent == "pi")
        {
            // Sent by the Bellboard extension for pi (installed by `bell hooks install`).
            switch (ev)
            {
                case "session_start":
                    pane.Agent = new AgentInfo("pi", sid, "idle", DateTime.Now, "Session started");
                    break;
                case "agent_start":
                case "ui_prompt_end":
                    pane.Agent = new AgentInfo("pi", sid, "working", DateTime.Now, Clip((string?)payload?["prompt"]) ?? pane.Agent?.Message);
                    pane.Attention = false;
                    break;
                case "ui_prompt_start":
                    var ask = (string?)payload?["message"] ?? "Pi is waiting for your answer";
                    pane.Agent = new AgentInfo("pi", sid, "waiting", DateTime.Now, ask);
                    _mux.Notifications.Post(pane, "Pi", ask, "pi");
                    break;
                case "agent_settled":
                    var said = Clip((string?)payload?["last_assistant_message"]);
                    var done = (bool?)payload?["aborted"] == true ? "Stopped" : said ?? "Finished — ready for your next prompt";
                    pane.Agent = new AgentInfo("pi", sid, "done", DateTime.Now, done);
                    _mux.Notifications.Post(pane, "Pi", done, "pi");
                    break;
                case "session_shutdown":
                    pane.Agent = null;
                    break;
            }
        }
        else if (agent == "codex")
        {
            var msg = (string?)payload?["last-assistant-message"] ?? "Turn complete";
            pane.Agent = new AgentInfo("codex", (string?)payload?["thread-id"], "done", DateTime.Now, Clip(msg));
            _mux.Notifications.Post(pane, "Codex", msg, "codex");
        }
        else
        {
            _mux.Notifications.Post(pane, agent, (string?)payload?["message"] ?? ev, agent);
        }
        pane.Workspace.Refresh();
        return new JsonObject { ["pane"] = pane.Id, ["event"] = ev };
    }

    private static string? Clip(string? s) =>
        s == null ? null : (s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim()).Length > 160 ? s[..159] + "…" : s;

    // ---- target resolution ----

    private Workspace Workspace(JsonObject p)
    {
        var key = Str(p, "workspace");
        if (key == null && _mux.FindPane(Str(p, "caller")) is { } caller) return caller.Workspace;
        return _mux.FindWorkspace(key) ?? throw new InvalidOperationException($"no workspace '{key}'");
    }

    /// <summary>--pane, else the pane the CLI was run from (BELLBOARD_PANE_ID), else the focused pane.</summary>
    private PaneBase Pane(JsonObject p)
    {
        if (Str(p, "pane") is { } id)
            return _mux.FindPane(id) ?? throw new InvalidOperationException($"no pane '{id}' (see `bell list`)");
        if (Str(p, "workspace") is { } wk)
            return (_mux.FindWorkspace(wk) ?? throw new InvalidOperationException($"no workspace '{wk}'")).FocusedPane
                   ?? throw new InvalidOperationException("workspace has no panes");
        return _mux.FindPane(Str(p, "caller")) ?? _mux.Selected?.FocusedPane ?? throw new InvalidOperationException("no pane is focused");
    }

    private TerminalPane Feed(JsonObject p) =>
        _mux.FindPane(Str(p, "pane")) as TerminalPane is { Feed: true } feed ? feed
            : throw new InvalidOperationException($"no feed pane '{Str(p, "pane")}'");

    private TerminalPane Terminal(JsonObject p) =>
        Pane(p) as TerminalPane ?? throw new InvalidOperationException("target pane is not a terminal");

    private BrowserPane Browser(JsonObject p, bool create = false, string? url = null)
    {
        PaneBase? anchor = null;
        if (Str(p, "pane") is { } id)
        {
            var named = _mux.FindPane(id) ?? throw new InvalidOperationException($"no pane '{id}'");
            if (named is BrowserPane namedBrowser) return namedBrowser;
            if (!create) throw new InvalidOperationException($"pane '{id}' is not a browser");
            anchor = named;
        }
        if (anchor == null && Str(p, "workspace") is { } wk) anchor = _mux.FindWorkspace(wk)?.FocusedPane;
        // From inside a pane the browser opens beside the caller; from outside, beside the largest pane.
        anchor ??= _mux.FindPane(Str(p, "caller")) ?? _mux.Selected?.Panes.OrderByDescending(x => x.Host.ActualWidth * x.Host.ActualHeight).FirstOrDefault()
                   ?? throw new InvalidOperationException("no pane is focused");
        if (anchor is BrowserPane bp) return bp;
        if (Str(p, "pane") == null && anchor.Workspace.Panes.OfType<BrowserPane>().FirstOrDefault() is { } existing) return existing;
        if (!create) throw new InvalidOperationException("no browser pane in this workspace — run `bell browser open <url>` first");
        var b = _mux.OpenBrowser(anchor, url, reuse: Str(p, "pane") == null);
        if (!Bool(p, "focus", true)) _mux.Focus(anchor);
        return b;
    }

    private static JsonObject StatusJson(Workspace ws) => new()
    {
        ["status"] = new JsonArray(ws.Status.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["text"] = s.Text, ["color"] = s.Color }).ToArray()),
        ["progress"] = ws.ProgressValue is { } v ? new JsonObject { ["value"] = v, ["label"] = ws.ProgressLabel } : null,
        ["logs"] = new JsonArray(ws.Logs.TakeLast(5).Select(l => (JsonNode)new JsonObject { ["level"] = l.Level, ["text"] = l.Text }).ToArray()),
    };

    private static double? Dbl(JsonObject p, string k)
    {
        if (p[k] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            bool pct = s.EndsWith('%');
            if (double.TryParse(pct ? s[..^1] : s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                return pct ? x / 100 : x;
        }
        return null;
    }

    private static JsonObject BrowserInfo(BrowserPane b) => new()
    {
        ["pane"] = b.Id,
        ["url"] = b.Url,
        ["title"] = b.Web.CoreWebView2?.DocumentTitle,
    };

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static bool Bool(JsonObject p, string k, bool dflt) => p[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : dflt;

    private static int Int(JsonObject p, string k, int dflt) =>
        p[k] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) ? j : dflt) : dflt;
}
