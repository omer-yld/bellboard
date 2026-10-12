using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bellboard.Shared;

namespace Bellboard.Cli;

/// <summary>
/// `bell mcp`: a Model Context Protocol server on stdio (newline-delimited JSON-RPC 2.0).
/// Each tool maps onto the Bellboard pipe API. Launched by an agent inside a Bellboard pane it inherits
/// BELLBOARD_PANE_ID, so tools default to the agent's own pane/workspace (a browser opens beside it).
/// </summary>
internal static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, JsonNode?> Run);

    private static PipeClient? _client;

    public static int Run()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var tools = Tools().ToDictionary(t => t.Name);

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch
            {
                Write(stdout, Error(null, -32700, "parse error"));
                continue;
            }
            if (msg == null) continue;
            var id = msg["id"]?.DeepClone();
            var method = (string?)msg["method"];
            if (method == null) continue;          // a response to us; we never send requests
            if (id == null) continue;              // notifications (initialized, cancelled, …) need no reply
            try
            {
                var p = msg["params"] as JsonObject ?? new JsonObject();
                JsonNode? result = method switch
                {
                    "initialize" => Initialize(p),
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray(tools.Values.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.Schema.DeepClone(),
                        }).ToArray()),
                    },
                    "tools/call" => CallTool(tools, p),
                    "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                    "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                    _ => throw new McpError(-32601, $"method not found: {method}"),
                };
                Write(stdout, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (McpError e)
            {
                Write(stdout, Error(id, e.Code, e.Message));
            }
            catch (Exception e)
            {
                Write(stdout, Error(id, -32603, e.Message));
            }
        }
        return 0;
    }

    private sealed class McpError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static void Write(StreamWriter w, JsonObject o) => w.WriteLine(o.ToJsonString());

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static JsonObject Initialize(JsonObject p)
    {
        var requested = (string?)p["protocolVersion"];
        return new JsonObject
        {
            ["protocolVersion"] = requested != null && SupportedVersions.Contains(requested) ? requested : ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "bellboard", ["version"] = BellboardProtocol.Version },
            ["instructions"] =
                "Bellboard is the terminal you are running in. Use these tools to open panes and workspaces, read or type into " +
                "other terminals, report progress in the sidebar (set_status, set_progress, log), notify the user, and drive a " +
                "real browser pane (browser_open, browser_snapshot returns element refs like e12 to click/type). Tools act on " +
                "your own pane unless you pass a pane id from list_workspaces.",
        };
    }

    private static JsonNode CallTool(Dictionary<string, Tool> tools, JsonObject p)
    {
        var name = (string?)p["name"] ?? throw new McpError(-32602, "missing tool name");
        if (!tools.TryGetValue(name, out var tool)) throw new McpError(-32602, $"unknown tool: {name}");
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var output = tool.Run(args);
            if (output is JsonObject o && o["__image"] is JsonValue img)
                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = (string?)img, ["mimeType"] = "image/png" }),
                };
            var text = output switch
            {
                null => "ok",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            };
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        }
        catch (Exception e)
        {
            _client?.Dispose();
            _client = null;
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = e.Message }),
            };
        }
    }

    /// <summary>Calls Bellboard with the caller's pane attached (reconnecting if Bellboard restarted).</summary>
    private static JsonNode? Call(string method, JsonObject args, int timeoutMs = 60_000)
    {
        _client ??= PipeClient.TryConnect(BellboardProtocol.ResolvePipeName(), 3000)
                    ?? throw new BellboardException("Bellboard is not running");
        if (args["caller"] == null && BellboardProtocol.Env(BellboardProtocol.EnvPane) is { Length: > 0 } caller) args["caller"] = caller;
        return _client.Call(method, args, timeoutMs);
    }

    // ---- tool table ----

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, t, d, _) in props)
        {
            properties[n] = t == "string[]"
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = d }
                : new JsonObject { ["type"] = t, ["description"] = d };
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = props.Where(x => x.Required).Select(x => (JsonNode)x.Name).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }

    private static JsonObject Pick(JsonObject args, params string[] keys)
    {
        var o = new JsonObject();
        foreach (var k in keys)
            if (args[k] is { } v) o[k] = v.DeepClone();
        return o;
    }

    private static readonly (string, string, string, bool) PaneArg = ("pane", "string", "Pane id from list_workspaces (default: your own pane)", false);

    private static IEnumerable<Tool> Tools()
    {
        yield return new("list_workspaces", "List workspaces and their panes (ids, kind, cwd, git branch, listening ports, agent status).",
            Schema(), _ => Call("workspace.list", new JsonObject()));
        yield return new("new_workspace", "Open a new workspace (sidebar tab) with a terminal, optionally running a command.",
            Schema(("name", "string", "Display name", false), ("cwd", "string", "Folder to start in", false),
                   ("command", "string", "Command to run in the new shell", false), ("shell", "string", "pwsh|powershell|cmd|bash|wsl|wsl:<distro>", false)),
            a => { var p = Pick(a, "name", "cwd", "command", "shell"); p["focus"] = false; return Call("workspace.new", p); });
        yield return new("split_pane", "Split a pane to open another terminal (or a browser with url) beside it.",
            Schema(("direction", "string", "right or down", false), PaneArg, ("command", "string", "Command to run in the new terminal", false),
                   ("cwd", "string", "Folder for the new terminal", false), ("url", "string", "Open a browser pane at this URL instead of a terminal", false)),
            a =>
            {
                var p = Pick(a, "direction", "pane", "command", "cwd", "url");
                if (p["url"] != null) p["kind"] = "browser";
                p["focus"] = false;
                return Call("pane.split", p);
            });
        yield return new("move_pane", "Move a pane (its shell keeps running) next to another pane, or into another or a new workspace.",
            Schema(PaneArg, ("to", "string", "Target pane id to place it next to", false),
                   ("side", "string", "left|right|up|down beside the target, or tab to join its tabs (default tab)", false),
                   ("into", "string", "Workspace id/name to move it into, or new", false)),
            a => Call("pane.move", Pick(a, "pane", "to", "side", "into")));
        yield return new("send_text", "Type text into a terminal pane (set enter=true to press Enter after it).",
            Schema(PaneArg, ("text", "string", "Text to type", true), ("enter", "boolean", "Press Enter afterwards", false)),
            a => Call("terminal.send", Pick(a, "pane", "text", "enter")));
        yield return new("send_keys", "Press keys in a terminal pane, e.g. [\"ctrl+c\"], [\"up\",\"enter\"], [\"esc\"].",
            Schema(PaneArg, ("keys", "string[]", "Key chords: enter tab esc backspace up down left right ctrl+<x> alt+<x> shift+tab f1-f12", true)),
            a => Call("terminal.send-key", Pick(a, "pane", "keys")));
        yield return new("read_screen", "Read the text of a terminal pane: the visible screen, or the last N lines including scrollback.",
            Schema(PaneArg, ("lines", "integer", "Last N lines (0 = visible screen)", false)),
            a => Call("terminal.read", Pick(a, "pane", "lines"))?["text"]);
        yield return new("notify", "Notify the user: rings the pane, adds an unread badge and a Windows toast.",
            Schema(("title", "string", "Short title", false), ("body", "string", "Message", true), PaneArg),
            a => Call("notify", Pick(a, "title", "body", "pane")));
        yield return new("set_status", "Show a status pill on your workspace's sidebar card (same key replaces it).",
            Schema(("key", "string", "Identifier, e.g. build", true), ("text", "string", "Pill text", true),
                   ("color", "string", "green|red|yellow|blue|purple|gray|#RRGGBB", false)),
            a => Call("status.set", Pick(a, "key", "text", "color")));
        yield return new("set_progress", "Show a progress bar on your workspace's sidebar card. value 0..1; pass clear=true to remove it.",
            Schema(("value", "number", "0..1", false), ("label", "string", "What is in progress", false), ("clear", "boolean", "Remove the bar", false)),
            a => a["clear"]?.GetValue<bool>() == true ? Call("progress.clear", new JsonObject()) : Call("progress.set", Pick(a, "value", "label")));
        yield return new("log", "Add a line to your workspace's sidebar log (the latest line is shown on the card).",
            Schema(("level", "string", "info|progress|success|warning|error", false), ("text", "string", "Log text", true)),
            a => Call("log.add", Pick(a, "level", "text")));
        yield return new("browser_open", "Open (or reuse) a browser pane beside your pane and load a URL; waits for the page to load.",
            Schema(("url", "string", "URL, host:port, or :3000 for localhost", false)),
            a => { var p = Pick(a, "url"); p["focus"] = false; return Call("browser.open", p); });
        yield return new("browser_navigate", "Navigate the browser pane to a URL and wait for it to load.",
            Schema(("url", "string", "URL", true), PaneArg), a => Call("browser.navigate", Pick(a, "url", "pane")));
        yield return new("browser_snapshot", "List the page's interactive elements and headings with refs (e.g. e12) for browser_click / browser_type.",
            Schema(PaneArg), a => Call("browser.snapshot", Pick(a, "pane"))?["snapshot"]);
        yield return new("browser_click", "Click an element by ref from browser_snapshot (e12), CSS selector, or text=Label.",
            Schema(("target", "string", "e12 | CSS selector | text=...", true), PaneArg), a => Call("browser.click", Pick(a, "target", "pane")));
        yield return new("browser_type", "Type into an input (focus it by target first). submit=true presses Enter.",
            Schema(("target", "string", "e12 | CSS selector", false), ("text", "string", "Text to type", true),
                   ("clear", "boolean", "Clear the field first", false), ("submit", "boolean", "Press Enter afterwards", false), PaneArg),
            a => Call("browser.type", Pick(a, "target", "text", "clear", "submit", "pane")));
        yield return new("browser_press", "Press a key in the page (Enter, Tab, Escape, ArrowDown, …).",
            Schema(("key", "string", "Key name", true), PaneArg), a => Call("browser.press", Pick(a, "key", "pane")));
        yield return new("browser_eval", "Evaluate JavaScript in the page (promises are awaited) and return the JSON result.",
            Schema(("js", "string", "Expression or IIFE", true), PaneArg), a => Call("browser.eval", Pick(a, "js", "pane"))?["value"] ?? "undefined");
        yield return new("browser_screenshot", "Screenshot the browser pane (PNG).",
            Schema(("full", "boolean", "Capture the full page, not just the viewport", false), PaneArg),
            a => new JsonObject { ["__image"] = (string?)Call("browser.screenshot", Pick(a, "full", "pane"))?["png"] });
        yield return new("browser_console", "Recent console messages and exceptions from the page.",
            Schema(("last", "integer", "How many (default 50)", false), PaneArg),
            a => { var p = Pick(a, "last", "pane"); p["last"] ??= 50; return Call("browser.console", p); });
        yield return new("browser_network", "Recent network responses and failures from the page.",
            Schema(("last", "integer", "How many (default 50)", false), PaneArg),
            a => { var p = Pick(a, "last", "pane"); p["last"] ??= 50; return Call("browser.network", p); });
        yield return new("pane_screenshot", "Screenshot any pane (terminal or browser) as PNG.",
            Schema(PaneArg), a => new JsonObject { ["__image"] = (string?)Call("pane.screenshot", Pick(a, "pane"))?["png"] });
        yield return new("agent_new", "Start another agent on a task in its own git worktree and branch (a new workspace).",
            Schema(("task", "string", "What the agent should do", true), ("agent", "string", "claude (default) | codex | opencode | pi", false),
                   ("base", "string", "Base branch (default: current)", false), ("repo", "string", "Repo folder (default: your cwd)", false)),
            a => { var p = Pick(a, "task", "agent", "base", "repo"); p["focus"] = false; return Call("agent.new", p, 120_000); });
        yield return new("agent_list", "List agent workspaces (task, branch, status).", Schema(), _ => Call("agent.list", new JsonObject()));
    }
}
