using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Bellboard.Shared;

namespace Bellboard.Cli;

internal static class Program
{
    private const string Help = """
        Bellboard — terminal multiplexer for AI coding agents on Windows

        usage: bell <command> [args] [--pane P] [--workspace W] [--json]

        Inside a Bellboard pane, commands target that pane by default (BELLBOARD_PANE_ID);
        otherwise they target the focused pane.

        app
          bell                          start Bellboard, or bring it to the front
          ping                          check that Bellboard is running
          quit                          save the session and exit
          screenshot -o out.png         capture the whole window
          screenshot --pane P -o f.png  capture one pane

        workspaces (vertical tabs)
          list | ls                     workspaces, panes, cwd, branch, ports, status
          new [--name N] [--cwd DIR] [--cmd "..."] [--shell S] [--no-focus]
          shells                        installed shells for --shell (pwsh, cmd, bash, wsl:Ubuntu…)
          select <id|index|name>
          rename [<workspace>] <name>
          close-workspace [<workspace>]
          pin workspace [<workspace>] · unpin workspace   first in the sidebar; won't close until unpinned
          move <up|down|top|bottom|N>   reorder (also drag in the sidebar, Ctrl+Shift+PgUp/PgDn)
          reopen                        bring back the last closed workspace or pane (Ctrl+Shift+Z)

        panes
          split [right|down] [--cwd DIR] [--cmd "..."] [--shell S] [--no-focus]
          tab new [--browser [URL]] [--cmd "..."] [--shell S] · tab next · tab prev
          zoom [on|off] · equalize · resize <left|right|up|down> [percent]
          move-pane <to-pane> [left|right|up|down|tab] [--pane P]   move a pane (shell keeps running)
          move-pane --into <workspace|new> [--pane P]               …to another or a new workspace
          flip [pane] · swap [pane]           side by side ⇄ top and bottom · swap the two sides
          feed open <name> [--badge B] [--label L] [--cwd DIR] [--focus]   a read-only pane a program reports into
          feed write --pane P <text> · feed status --pane P <working|waiting|done|idle> [message] [--kind K]
          pin tab [--pane P] · unpin tab      first in its tab row, always shown; won't close until unpinned
          find                                open the find bar in a terminal
          split [right|down] --browser [URL]
          focus <pane>
          close [--pane P]
          send [--pane P] [--enter] <text>    escapes: \n \r \t \e \xHH  (--stdin: pipe text verbatim)
          broadcast [on|off]                  type into every terminal of the workspace
          send-key [--pane P] <key...>        enter tab esc ctrl+c shift+tab up alt+x f5 …
          read [--pane P] [--lines N]         visible screen, or last N lines

        agents (each in its own git worktree + branch)
          agent new <task> [--agent claude|codex|opencode|pi|"cmd"] [--base B] [--branch NAME] [--repo DIR] [--name N] [--shell S]
          agent list · agent info [--workspace W] · inbox    (who needs input, for how long)
          agent finish --merge|--pr|--keep|--discard [--force] [--workspace W]

        sidebar status (shown on the workspace's card; targets the caller's workspace)
          status set <key> <text> [--color green|red|yellow|blue|gray|#hex] · status clear [key] · status list
          progress <0..1|NN%> [--label text] · progress clear
          log [--level info|progress|success|warning|error] <text> · log list · log clear
          sidebar-state                       cwd, branch, ports, status, progress, logs (JSON)

        notifications
          notify [--title T] <body>           ring + badge + toast
          notifications                       list recent notifications
          notifications clear | jump
          dnd [on|off]                        do not disturb (no toasts/flashes; badges still update)
          mute [on|off] [--workspace W]       mute one workspace's toasts
          palette [query]                     open the command palette (Ctrl+Shift+P)

        browser (WebView2 pane, driven over the DevTools protocol)
          browser open [URL]                  open/reuse a browser split
          browser navigate <URL>   |  back | forward | reload | url
          browser snapshot                    interactive elements with refs (e12)
          browser click <ref|css|text=...>
          browser type <ref|css> <text> [--clear] [--submit]
          browser press <Enter|Tab|Escape|...>
          browser eval <js>                   awaits promises, returns JSON
          browser wait [--selector S] [--text T] [--timeout MS]
          browser console [--last N] [--clear]
          browser network [--last N] [--clear]
          browser screenshot -o out.png [--full]

        agents
          hooks install | uninstall | status  Claude Code hooks (+ Codex notify); add --wsl for agents in WSL
          mcp                                 MCP server on stdio (tools for panes, status, browser, agents)
          mcp install                         register it with Claude Code (claude mcp add --scope user)

        maintenance
          update [--check] [--feed URL|path] [--yes]   download, verify (SHA-256) and install a newer build
          doctor [--zip] [--include-session]           diagnostics; --zip writes a bundle to the Desktop
          skill install                       add the Bellboard skill to ~/.claude/skills
          hook claude <Event>                 (called by the hooks; reads JSON on stdin)
          hook codex <json>                   (called by Codex `notify`)
        """;

    private static readonly HashSet<string> Flags = ["json", "enter", "browser", "no-focus", "clear", "submit", "full", "help", "fresh", "screen", "stdin",
        "merge", "pr", "keep", "discard", "force", "check", "yes", "zip", "include-session", "wsl", "focus"];

    private static int Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return Run(argv);
        }
        catch (BellboardException e)
        {
            Console.Error.WriteLine("bell: " + e.Message);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Console.Error.WriteLine("bell: " + e.Message);
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        if (argv.Length == 0) return Launch(Array.Empty<string>());
        var cmd = argv[0];
        var a = new Args(argv.Skip(1).ToArray(), Flags);

        switch (cmd)
        {
            case "help" or "-h" or "--help" or "/?":
                Console.WriteLine(Help);
                return 0;
            case "version" or "--version" or "-v":
                Console.WriteLine($"Bellboard {BellboardProtocol.Version}");
                return 0;
            case "hook":
                return Hooks.RunHook(argv.Skip(1).ToArray());
            case "hooks":
                return Hooks.Manage(a.Pos(0) ?? "status", a.Has("wsl"));
            case "mcp":
                return a.Pos(0) == "install" ? Hooks.InstallMcp() : McpServer.Run();
            case "skill":
                return Hooks.InstallSkill(a.Pos(0) ?? "install");
            case "update":
                return Maintenance.Update(a);
            case "doctor":
                return Maintenance.Doctor(a);
            case "launch" or "start" or "open":
                return Launch(a.Has("fresh") ? ["--fresh"] : Array.Empty<string>());
        }

        using var client = Connect();
        var p = a.Common();
        switch (cmd)
        {
            case "ping":
            {
                var r = client.Call("system.ping", p)!;
                if (a.Has("json")) return Json(r);
                Console.WriteLine($"pong — Bellboard {r["version"]} (pid {r["pid"]}, WebView2 {r["webview2"]}, ConPTY {r["conpty"]}, {r["workspaces"]} workspace(s), toasts {r["toasts"]})");
                return 0;
            }
            case "quit" or "exit":
                client.Call("app.quit", p);
                return 0;
            case "list" or "ls":
            {
                var r = client.Call("workspace.list", p)!;
                if (a.Has("json")) return Json(r);
                PrintList(r);
                return 0;
            }
            case "new":
                p["name"] = a.Opt("name") ?? a.Pos(0);
                // Inside a pane, a new workspace opens where the command was run.
                p["cwd"] = FullPath(a.Opt("cwd")) ?? (p["caller"] != null ? Environment.CurrentDirectory : null);
                p["command"] = a.Opt("cmd") ?? a.Opt("command");
                p["shell"] = a.Opt("shell");
                p["focus"] = !a.Has("no-focus");
                return Print(client.Call("workspace.new", p), a, r => (string?)r?["workspace"] + " " + (string?)r?["pane"]);
            case "select":
                p["workspace"] = a.Pos(0) ?? throw new BellboardException("usage: bell select <workspace>");
                return Print(client.Call("workspace.select", p), a, r => (string?)r?["workspace"]);
            case "rename":
                if (a.Count >= 2) { p["workspace"] = a.Pos(0); p["name"] = string.Join(" ", a.Positional.Skip(1)); }
                else p["name"] = a.Pos(0) ?? "";
                return Print(client.Call("workspace.rename", p), a, r => $"{r?["workspace"]} → {r?["name"]}");
            case "close-workspace":
                if (a.Pos(0) != null) p["workspace"] = a.Pos(0);
                client.Call("workspace.close", p);
                return 0;
            case "pin" or "unpin":
            {
                // bell pin tab [--pane P] | bell pin workspace [W]
                var what = a.Pos(0) ?? throw new BellboardException($"usage: bell {cmd} tab [--pane P] | bell {cmd} workspace [<workspace>]");
                p["on"] = cmd == "pin";
                if (what is "workspace" or "ws")
                {
                    if (a.Pos(1) != null) p["workspace"] = a.Pos(1);
                    return Print(client.Call("workspace.pin", p), a, r => $"{r?["workspace"]} {((bool?)r?["pinned"] == true ? "pinned" : "unpinned")}");
                }
                if (what is not ("tab" or "pane")) throw new BellboardException($"usage: bell {cmd} tab [--pane P] | bell {cmd} workspace [<workspace>]");
                if (a.Pos(1) != null) p["pane"] = a.Pos(1);
                return Print(client.Call("pane.pin", p), a, r => $"{r?["pane"]} {((bool?)r?["pinned"] == true ? "pinned" : "unpinned")}");
            }
            case "next":
                client.Call("workspace.next", p);
                return 0;
            case "move":
                p["to"] = a.Pos(0) ?? throw new BellboardException("usage: bell move <up|down|top|bottom|N> [--workspace W]");
                return Print(client.Call("workspace.move", p), a, r => $"{r?["workspace"]} is now #{r?["index"]}");
            case "reopen":
                return Print(client.Call("app.reopen", p), a, r => (string?)r?["result"]);
            case "dev-menu":
                p["which"] = a.Pos(0) ?? "workspace";
                client.Call("dev.menu", p);
                return 0;
            case "feed":
                // A read-only pane another program reports into: bell feed open <name> → pane id; write; status.
                switch (a.Pos(0))
                {
                    case "open":
                        p["name"] = a.Opt("name") ?? a.Pos(1) ?? "Feed";
                        p["cwd"] = FullPath(a.Opt("cwd"));
                        p["badge"] = a.Opt("badge");
                        p["label"] = a.Opt("label");
                        p["focus"] = a.Has("focus");
                        return Print(client.Call("feed.open", p), a, r => (string?)r?["pane"]);
                    case "write":
                        p["text"] = a.Has("stdin") ? StdinText() : Unescape(string.Join(" ", a.Positional.Skip(1))) + "\n";
                        client.Call("feed.write", p);
                        return 0;
                    case "status":
                        p["status"] = a.Pos(1) ?? throw new BellboardException("usage: bell feed status --pane P <working|waiting|done|idle> [message] [--kind K]");
                        p["message"] = a.Count > 2 ? string.Join(" ", a.Positional.Skip(2)) : null;
                        p["kind"] = a.Opt("kind");
                        client.Call("feed.status", p);
                        return 0;
                    default:
                        throw new BellboardException("usage: bell feed open <name> [--badge B] [--label L] [--cwd DIR] [--focus] | feed write --pane P <text> | feed status --pane P <state> [message]");
                }
            case "dev-drag":
                p["step"] = a.Pos(0) ?? "cancel";
                if (a.Pos(0) == "move") { p["x"] = a.Pos(1); p["y"] = a.Pos(2); }
                return Print(client.Call("dev.drag", p), a, r => r?.ToJsonString() ?? "");
            case "split":
            {
                var dir = a.Pos(0) is "down" or "right" or "left" or "up" ? a.Pos(0)! : "right";
                p["direction"] = dir;
                p["cwd"] = FullPath(a.Opt("cwd"));
                p["command"] = a.Opt("cmd") ?? a.Opt("command");
                p["shell"] = a.Opt("shell");
                p["focus"] = !a.Has("no-focus");
                if (a.Has("browser") || a.Opt("url") != null)
                {
                    p["kind"] = "browser";
                    p["url"] = a.Opt("url") ?? a.Positional.FirstOrDefault(x => x is not ("down" or "right" or "left" or "up"));
                }
                return Print(client.Call("pane.split", p), a, r => (string?)r?["pane"]);
            }
            case "tab":
                switch (a.Pos(0))
                {
                    case "new":
                        p["cwd"] = FullPath(a.Opt("cwd"));
                        p["command"] = a.Opt("cmd") ?? a.Opt("command");
                        p["shell"] = a.Opt("shell");
                        p["focus"] = !a.Has("no-focus");
                        if (a.Has("browser") || a.Opt("url") != null) { p["kind"] = "browser"; p["url"] = a.Opt("url") ?? a.Pos(1); }
                        return Print(client.Call("tab.new", p), a, r => (string?)r?["pane"]);
                    case "next" or "prev":
                        p["delta"] = a.Pos(0) == "next" ? 1 : -1;
                        client.Call("tab.next", p);
                        return 0;
                    default:
                        throw new BellboardException("usage: bell tab new [--browser [URL]] [--cmd ...] [--shell S] | tab next | tab prev");
                }
            case "zoom":
                if (a.Pos(0) is "on" or "off") p["on"] = a.Pos(0) == "on";
                return Print(client.Call("pane.zoom", p), a, r => (bool?)r?["zoomed"] == true ? "zoomed" : "unzoomed");
            case "equalize":
                client.Call("layout.equalize", p);
                return 0;
            case "move-pane":
                if (a.Opt("into") is { } into) p["into"] = into;
                else
                {
                    p["to"] = a.Pos(0) ?? throw new BellboardException("usage: bell move-pane <to-pane> [left|right|up|down|tab] [--pane P] | bell move-pane --into <workspace|new> [--pane P]");
                    p["side"] = a.Pos(1) ?? "tab";
                    if (int.TryParse(a.Opt("index"), out var at)) p["index"] = at;
                }
                return Print(client.Call("pane.move", p), a, r => $"{r?["pane"]} is in {r?["workspace"]}");
            case "flip" or "swap":
                if (a.Pos(0) != null) p["pane"] = a.Pos(0);
                return Print(client.Call("layout." + cmd, p), a, r => (string?)r?["layout"]);
            case "resize":
                p["direction"] = a.Pos(0) ?? throw new BellboardException("usage: bell resize <left|right|up|down> [percent]");
                if (int.TryParse(a.Pos(1), out var pct)) p["percent"] = pct;
                client.Call("pane.resize", p);
                return 0;
            case "find":
                client.Call("terminal.find", p);
                return 0;
            case "shells":
            {
                var r = client.Call("shell.list", p)!.AsArray();
                if (a.Has("json")) return Json(r);
                foreach (var s in r) Console.WriteLine($"{s?["alias"],-22} {s?["label"]}");
                return 0;
            }
            case "focus":
                p["pane"] = a.Opt("pane") ?? a.Pos(0) ?? throw new BellboardException("usage: bell focus <pane>");
                client.Call("pane.focus", p);
                return 0;
            case "close":
                if (a.Pos(0) != null) p["pane"] = a.Pos(0);
                client.Call("pane.close", p);
                return 0;
            case "send":
                // --stdin sends piped text verbatim (no escapes, no shell quoting problems).
                p["text"] = a.Has("stdin") ? StdinText() : Unescape(string.Join(" ", a.Positional));
                p["enter"] = a.Has("enter");
                return Print(client.Call("terminal.send", p), a, _ => null);
            case "send-key" or "key":
                p["keys"] = new JsonArray(a.Positional.Select(k => (JsonNode)k).ToArray());
                return Print(client.Call("terminal.send-key", p), a, _ => null);
            case "read":
            {
                p["lines"] = int.TryParse(a.Opt("lines"), out var n) ? n : 0;
                var r = client.Call("terminal.read", p);
                if (a.Has("json")) return Json(r);
                Console.WriteLine((string?)r?["text"]);
                return 0;
            }
            case "notify":
                p["title"] = a.Opt("title");
                p["body"] = string.Join(" ", a.Positional);
                if (string.IsNullOrEmpty((string?)p["body"]) && string.IsNullOrEmpty((string?)p["title"])) throw new BellboardException("usage: bell notify [--title T] <body>");
                return Print(client.Call("notify", p), a, _ => null);
            case "agent":
                return Agent(client, a, p);
            case "inbox":
            {
                var r = client.Call("agent.inbox", p)!.AsArray();
                if (a.Has("json")) return Json(r);
                if (r.Count == 0) Console.WriteLine("no agents");
                foreach (var x in r)
                {
                    var since = DateTime.TryParse((string?)x?["since"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? $" · {(int)(DateTime.Now - t).TotalMinutes}m" : "";
                    Console.WriteLine($"{(string?)x?["status"],-8}{since,-6} {x?["pane"],-4} {x?["title"]}");
                    if ((string?)x?["message"] is { Length: > 0 } m) Console.WriteLine($"                 {m}");
                }
                return 0;
            }
            case "status":
            {
                var sub = a.Pos(0) ?? "list";
                switch (sub)
                {
                    case "set":
                        p["key"] = a.Pos(1) ?? throw new BellboardException("usage: bell status set <key> <text> [--color green|red|yellow|blue|gray|#RRGGBB]");
                        p["text"] = string.Join(" ", a.Positional.Skip(2));
                        p["color"] = a.Opt("color");
                        client.Call("status.set", p);
                        return 0;
                    case "clear":
                        if (a.Pos(1) != null) p["key"] = a.Pos(1);
                        client.Call("status.clear", p);
                        return 0;
                    default:
                        return Json(client.Call("status.list", p));
                }
            }
            case "progress":
                if (a.Pos(0) is "clear" or "done") { client.Call("progress.clear", p); return 0; }
                p["value"] = a.Pos(0) ?? throw new BellboardException("usage: bell progress <0..1|NN%> [--label text] | bell progress clear");
                p["label"] = a.Opt("label") ?? (a.Count > 1 ? string.Join(" ", a.Positional.Skip(1)) : null);
                client.Call("progress.set", p);
                return 0;
            case "log":
                if (a.Pos(0) == "clear") { client.Call("log.clear", p); return 0; }
                if (a.Pos(0) == "list")
                {
                    p["limit"] = int.TryParse(a.Opt("limit"), out var lim) ? lim : 50;
                    var logs = client.Call("log.list", p)!.AsArray();
                    if (a.Has("json")) return Json(logs);
                    foreach (var l in logs) Console.WriteLine($"[{l?["level"]}] {l?["text"]}");
                    return 0;
                }
                p["level"] = a.Opt("level") ?? "info";
                p["text"] = string.Join(" ", a.Positional);
                client.Call("log.add", p);
                return 0;
            case "sidebar-state":
                return Json(client.Call("sidebar.state", p));
            case "dnd":
                if (a.Pos(0) is "on" or "off") p["on"] = a.Pos(0) == "on";
                return Print(client.Call("notification.dnd", p), a, r => (bool?)r?["doNotDisturb"] == true ? "do not disturb: on" : "do not disturb: off");
            case "broadcast":
                if (a.Pos(0) is "on" or "off") p["on"] = a.Pos(0) == "on";
                return Print(client.Call("workspace.broadcast", p), a, r => (bool?)r?["broadcast"] == true ? "broadcasting input to all terminals" : "broadcast off");
            case "mute":
                if (a.Pos(0) is "on" or "off") p["on"] = a.Pos(0) == "on";
                return Print(client.Call("workspace.mute", p), a, r => $"{r?["workspace"]} {((bool?)r?["muted"] == true ? "muted" : "unmuted")}");
            case "palette":
                p["query"] = string.Join(" ", a.Positional);
                client.Call("app.palette", p);
                return 0;
            case "dev-unload":
                return Json(client.Call("dev.unload", p, 60_000));
            case "dev-lowmem":
                p["on"] = a.Pos(0) != "off";
                p["suspend"] = a.Has("force");
                return Json(client.Call("dev.lowmem", p));
            case "dev-toasts":
                return Json(client.Call("dev.toasts", p));
            case "dev-toast-activate":
                p["argument"] = a.Pos(0);
                client.Call("dev.toast-activate", p);
                return 0;
            case "notifications":
            {
                if (a.Pos(0) == "clear") { client.Call("notification.clear", p); return 0; }
                if (a.Pos(0) == "jump") { client.Call("notification.jump", p); return 0; }
                var r = client.Call("notification.list", p)!.AsArray();
                if (a.Has("json")) return Json(r);
                foreach (var n in r)
                {
                    var at = DateTime.TryParse((string?)n?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToString("HH:mm:ss") : "";
                    Console.WriteLine($"{((bool?)n?["read"] == true ? " " : "●")} {at}  {n?["workspace"]}/{n?["pane"]}  {n?["title"]}: {n?["body"]}");
                }
                return 0;
            }
            case "screenshot":
            {
                var outPath = a.Opt("o") ?? a.Opt("out") ?? a.Pos(0) ?? $"bellboard-{DateTime.Now:yyyyMMdd-HHmmss}.png";
                p["screen"] = a.Has("screen");
                var r = a.Opt("pane") != null ? client.Call("pane.screenshot", p) : client.Call("app.screenshot", p);
                return SavePng(r, outPath);
            }
            case "browser":
                return Browser(client, a, p);
            default:
                throw new BellboardException($"unknown command '{cmd}' (see `bell help`)");
        }
    }

    private static int Agent(PipeClient client, Args a, JsonObject p)
    {
        switch (a.Pos(0))
        {
            case "new":
            {
                p["task"] = a.Has("stdin") ? StdinText() : string.Join(" ", a.Positional.Skip(1));
                p["agent"] = a.Opt("agent");
                p["repo"] = FullPath(a.Opt("repo")) ?? (p["caller"] != null ? Environment.CurrentDirectory : null);
                p["base"] = a.Opt("base");
                p["branch"] = a.Opt("branch");
                p["name"] = a.Opt("name");
                p["shell"] = a.Opt("shell");
                p["focus"] = !a.Has("no-focus");
                return Print(client.Call("agent.new", p, 120_000), a, r => $"{r?["workspace"]}  {r?["branch"]}  {r?["worktree"]}");
            }
            case "list" or "ls" or null:
            {
                var r = client.Call("agent.list", p)!.AsArray();
                if (a.Has("json")) return Json(r);
                if (r.Count == 0) Console.WriteLine("no agent workspaces (start one with: bell agent new \"<task>\")");
                foreach (var x in r)
                    Console.WriteLine($"{x?["workspace"],-4} {x?["status"] ?? "-",-8} {x?["branch"]}  ← {x?["base"]}   {x?["task"]}");
                return 0;
            }
            case "info":
                return Json(client.Call("agent.info", p, 60_000));
            case "finish":
            {
                var mode = new[] { "merge", "pr", "keep", "discard" }.FirstOrDefault(a.Has)
                           ?? throw new BellboardException("usage: bell agent finish --merge|--pr|--keep|--discard [--force] [--workspace W]");
                p["mode"] = mode;
                p["force"] = a.Has("force");
                return Print(client.Call("agent.finish", p, 180_000), a, r => (string?)r?["result"]);
            }
            default:
                throw new BellboardException("usage: bell agent new <task> | list | info | finish --merge|--pr|--keep|--discard");
        }
    }

    private static int Browser(PipeClient client, Args a, JsonObject p)
    {
        var sub = a.Pos(0) ?? throw new BellboardException("usage: bell browser <open|navigate|snapshot|click|type|press|eval|wait|console|network|screenshot|url|back|forward|reload>");
        string Arg(int i, string usage) => a.Pos(i) ?? throw new BellboardException("usage: bell browser " + usage);
        if (a.Opt("timeout") is { } to) p["timeout"] = int.Parse(to, CultureInfo.InvariantCulture);
        switch (sub)
        {
            case "open":
                p["url"] = a.Pos(1);
                p["focus"] = !a.Has("no-focus");
                return Print(client.Call("browser.open", p), a, PageLine);
            case "navigate" or "goto" or "go":
                p["url"] = Arg(1, "navigate <url>");
                return Print(client.Call("browser.navigate", p), a, PageLine);
            case "back" or "forward" or "reload" or "url":
                return Print(client.Call("browser." + sub, p), a, PageLine);
            case "snapshot":
            {
                var r = client.Call("browser.snapshot", p);
                if (a.Has("json")) return Json(r);
                Console.WriteLine((string?)r?["snapshot"]);
                return 0;
            }
            case "click":
                p["target"] = Arg(1, "click <ref|css|text=...>");
                return Print(client.Call("browser.click", p), a, PageLine);
            case "type":
                if (a.Has("stdin")) { p["target"] = a.Pos(1); p["text"] = StdinText(); }
                else if (a.Count >= 3) { p["target"] = a.Pos(1); p["text"] = Unescape(string.Join(" ", a.Positional.Skip(2))); }
                else p["text"] = Unescape(Arg(1, "type <ref|css> <text>"));
                p["clear"] = a.Has("clear");
                p["submit"] = a.Has("submit");
                return Print(client.Call("browser.type", p), a, PageLine);
            case "press":
                p["key"] = Arg(1, "press <key>");
                return Print(client.Call("browser.press", p), a, PageLine);
            case "eval" or "js":
            {
                p["js"] = a.Has("stdin") ? StdinText() : string.Join(" ", a.Positional.Skip(1));
                var r = client.Call("browser.eval", p);
                var v = r?["value"];
                Console.WriteLine(v is JsonValue jv && jv.TryGetValue<string>(out var s) && !a.Has("json") ? s : v?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "undefined");
                return 0;
            }
            case "wait":
                p["selector"] = a.Opt("selector");
                p["text"] = a.Opt("text");
                return Print(client.Call("browser.wait", p), a, PageLine);
            case "console" or "network":
            {
                p["last"] = int.TryParse(a.Opt("last"), out var n) ? n : 100;
                p["clear"] = a.Has("clear");
                var r = client.Call("browser." + sub, p)!.AsArray();
                if (a.Has("json")) return Json(r);
                foreach (var e in r)
                {
                    Console.WriteLine(sub == "console"
                        ? $"{e?["at"]} [{e?["level"]}] {e?["text"]}"
                        : $"{e?["at"]} {(e?["error"] != null ? "ERR " + e["error"] : e?["status"]?.ToString())} {e?["url"]}");
                }
                return 0;
            }
            case "screenshot":
                p["full"] = a.Has("full");
                return SavePng(client.Call("browser.screenshot", p), a.Opt("o") ?? a.Opt("out") ?? a.Pos(1) ?? $"bellboard-browser-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            default:
                throw new BellboardException($"unknown browser command '{sub}'");
        }
    }

    // ---- helpers ----

    private static PipeClient Connect()
    {
        var name = BellboardProtocol.ResolvePipeName();
        return PipeClient.TryConnect(name, 3000)
               ?? throw new BellboardException($@"Bellboard is not running (no \\.\pipe\{name}). Start it with `bell`.");
    }

    private static int Launch(string[] args)
    {
        var name = BellboardProtocol.ServerPipeName();
        using (var existing = PipeClient.TryConnect(name, 300))
        {
            if (existing != null)
            {
                existing.Call("app.activate");
                return 0;
            }
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "Bellboard.exe");
        if (!File.Exists(exe)) throw new BellboardException($"Bellboard.exe not found next to bell.exe ({AppContext.BaseDirectory})");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Process.Start(psi);
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            using var c = PipeClient.TryConnect(name, 100);
            if (c != null)
            {
                Console.WriteLine("Bellboard started");
                return 0;
            }
        }
        Console.Error.WriteLine("bell: started, but the pipe did not come up within 10s (see %LOCALAPPDATA%\\Bellboard\\bellboard.log)");
        return 1;
    }

    private static void PrintList(JsonNode r)
    {
        foreach (var w in r["workspaces"]!.AsArray())
        {
            var sel = (bool?)w?["selected"] == true ? "*" : " ";
            var meta = new List<string>();
            if ((string?)w?["branch"] is { } b) meta.Add("⎇ " + b);
            if ((string?)w?["cwd"] is { } c) meta.Add(c);
            var ports = w?["ports"]?.AsArray().Select(x => ":" + x).ToList() ?? [];
            if (ports.Count > 0) meta.Add(string.Join(" ", ports));
            var unread = ((bool?)w?["pinned"] == true ? "  [pinned]" : "") + ((int?)w?["unread"] is > 0 and var u ? $"  [{u} unread]" : "");
            Console.WriteLine($"{sel} {w?["id"],-4} {w?["index"]}. {w?["name"]}{unread}{(meta.Count > 0 ? "   (" + string.Join(" · ", meta) + ")" : "")}");
            if ((string?)w?["status"] is { } st) Console.WriteLine($"         {st}");
            if ((string?)w?["notice"] is { } n) Console.WriteLine($"         “{n}”");
            foreach (var p in w?["panes"]?.AsArray() ?? new JsonArray())
            {
                var flags = new List<string>();
                if ((string?)p?["tab"] is { } tab) flags.Add("tab " + tab + ((bool?)p?["active"] == true ? " active" : ""));
                if ((bool?)p?["pinned"] == true) flags.Add("pinned");
                if ((bool?)p?["focused"] == true) flags.Add("focused");
                if ((bool?)p?["attention"] == true) flags.Add("attention");
                var detail = (string?)p?["kind"] == "browser" ? (string?)p?["url"] : (string?)p?["cwd"];
                var title = (string?)p?["title"];
                Console.WriteLine($"      {p?["id"],-4} {p?["kind"],-8} {detail}{(title != null ? "  — " + title : "")}{(flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : "")}");
            }
        }
    }

    private static string? PageLine(JsonNode? r) => $"{r?["pane"]}  {r?["url"]}  {(string?)r?["title"]}";

    private static int Print(JsonNode? r, Args a, Func<JsonNode?, string?> human)
    {
        if (a.Has("json")) return Json(r);
        if (human(r) is { Length: > 0 } s) Console.WriteLine(s);
        return 0;
    }

    private static int Json(JsonNode? r)
    {
        Console.WriteLine(r?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
        return 0;
    }

    private static int SavePng(JsonNode? r, string path)
    {
        var b64 = (string?)r?["png"] ?? throw new BellboardException("no image returned");
        var full = Path.GetFullPath(path);
        File.WriteAllBytes(full, Convert.FromBase64String(b64));
        Console.WriteLine(full);
        return 0;
    }

    private static string StdinText()
    {
        if (!Console.IsInputRedirected) throw new BellboardException("--stdin needs piped input");
        return Console.In.ReadToEnd().TrimEnd('\r', '\n');
    }

    private static string? FullPath(string? p) => string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p);

    internal static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i == s.Length - 1) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'e': sb.Append('\x1b'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < s.Length && int.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var hex):
                    sb.Append((char)hex);
                    i += 2;
                    break;
                default: sb.Append('\\').Append(c); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Tiny argv parser: positionals, --key value, --flag, -o value.</summary>
internal sealed class Args
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv, HashSet<string> flags)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (t == "--")
            {
                Positional.AddRange(argv.Skip(i + 1));
                break;
            }
            if (t.StartsWith("--") && t.Length > 2)
            {
                var key = t[2..];
                var eq = key.IndexOf('=');
                if (eq > 0) _opts[key[..eq]] = key[(eq + 1)..];
                else if (flags.Contains(key)) _opts[key] = null;
                else if (key == "browser" || i + 1 >= argv.Length) _opts[key] = null;
                else _opts[key] = argv[++i];
            }
            else if (t == "-o" && i + 1 < argv.Length) _opts["o"] = argv[++i];
            else Positional.Add(t);
        }
    }

    public int Count => Positional.Count;
    public string? Pos(int i) => i < Positional.Count ? Positional[i] : null;
    public string? Opt(string k) => _opts.TryGetValue(k, out var v) ? v : null;
    public bool Has(string k) => _opts.ContainsKey(k);

    /// <summary>Targeting options shared by every command, plus the caller's pane from the environment.</summary>
    public JsonObject Common()
    {
        var p = new JsonObject();
        if (Opt("pane") is { } pane) p["pane"] = pane;
        if (Opt("workspace") is { } ws) p["workspace"] = ws;
        if (BellboardProtocol.Env(BellboardProtocol.EnvPane) is { Length: > 0 } caller) p["caller"] = caller;
        return p;
    }
}
