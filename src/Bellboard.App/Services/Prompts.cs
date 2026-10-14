using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Bellboard.Core;
using Bellboard.Ipc;
using Bellboard.Panes;

namespace Bellboard.Services;

/// <summary>A Claude Code permission request (PermissionRequest hook) waiting for an answer from the phone.</summary>
public sealed class PendingPrompt
{
    private static int _counter;
    public string Id { get; } = "q" + System.Threading.Interlocked.Increment(ref _counter);
    public required TerminalPane Pane { get; init; }
    public required string Tool { get; init; }
    public required string Summary { get; init; }
    public string? Detail { get; init; }
    /// <summary>Claude's "always allow" suggestions, passed back as updatedPermissions for "Allow always".</summary>
    public JsonArray? Suggestions { get; init; }
    public bool CanAlwaysAllow => Suggestions is { Count: > 0 };
    public DateTime Since { get; } = DateTime.Now;
    internal TaskCompletionSource<JsonObject?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Routes Claude Code permission requests to paired phones while you're away from the desk.
///
/// `bell hook claude PermissionRequest` calls `agent.permission` and waits. When the request shouldn't go to
/// the phone (remote off, no device that can answer, you're at the desk) it gets "no decision" at once and
/// Claude Code shows its own dialog as usual. Otherwise it waits for Allow / Deny from the phone or the
/// desktop inbox. It never allows anything by itself: a timeout, a new event from the agent, the pane
/// closing or you coming back to the desk all hand the question back to the terminal (no decision).
/// </summary>
public sealed class PromptBroker
{
    private readonly MuxController _mux;
    private readonly List<PendingPrompt> _pending = new();

    public PromptBroker(MuxController mux) => _mux = mux;

    public IReadOnlyList<PendingPrompt> Pending => _pending;

    public PendingPrompt? For(PaneBase pane) => _pending.LastOrDefault(p => p.Pane == pane);

    public PendingPrompt? Find(string? id) => _pending.FirstOrDefault(p => p.Id == id);

    /// <summary>Handles a PermissionRequest hook; returns Claude Code's "decision" object, or null for no decision.</summary>
    public async Task<JsonObject?> RequestAsync(TerminalPane pane, JsonObject? payload, TimeSpan wait)
    {
        if (!_mux.Remote.ShouldRoutePrompts) return null;
        var tool = (string?)payload?["tool_name"] ?? "tool";
        var input = payload?["tool_input"] as JsonObject;
        var (summary, detail) = Describe(tool, input);
        var q = new PendingPrompt
        {
            Pane = pane,
            Tool = tool,
            Summary = summary,
            Detail = detail,
            Suggestions = payload?["permission_suggestions"] is JsonArray s && s.Count > 0 ? (JsonArray)s.DeepClone() : null,
        };
        // One question per pane at a time: a new one means the old one is no longer being asked.
        foreach (var old in _pending.Where(p => p.Pane == pane).ToList()) Finish(old, null);
        _pending.Add(q);
        Log.Info($"permission request {q.Id} in {pane.Id}: {tool} {summary}");
        // The agent is waiting for you now (Claude's own permission_prompt notification comes ~6 s later).
        var kind = pane.Agent?.Kind ?? "claude";
        pane.Agent = new AgentInfo(kind, pane.Agent?.SessionId, "waiting", DateTime.Now,
            $"{AgentManager.DisplayName(kind)} wants to use {tool}{(summary.Length > 0 ? ": " + summary : "")}");
        _mux.Events.Publish("prompt", "pending", new JsonObject
        {
            ["pane"] = pane.Id, ["workspace"] = pane.Workspace.Id, ["workspaceName"] = pane.Workspace.Name,
            ["agent"] = AgentManager.DisplayName(pane.Agent?.Kind ?? "claude"), ["prompt"] = StateJson.Prompt(q),
        });
        _mux.Events.MarkDirty();

        var timeout = Task.Delay(wait);
        var done = await Task.WhenAny(q.Answer.Task, timeout);
        if (done == timeout) Finish(q, null);
        return await q.Answer.Task;
    }

    /// <summary>Allow / deny / always from the phone or the desktop. False if it was already answered.</summary>
    public bool Resolve(string id, string decision, string? message, string who)
    {
        var q = Find(id);
        if (q == null) return false;
        JsonObject answer = decision switch
        {
            "allow" => new JsonObject { ["behavior"] = "allow" },
            "always" => q.Suggestions is { } s
                ? new JsonObject { ["behavior"] = "allow", ["updatedPermissions"] = s.DeepClone() }
                : new JsonObject { ["behavior"] = "allow" },
            "deny" => new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = string.IsNullOrWhiteSpace(message) ? $"The user denied this from {who}." : message.Trim(),
            },
            _ => throw new InvalidOperationException("decision must be allow, deny or always"),
        };
        var verb = decision switch { "allow" => "allowed", "always" => "always allowed", _ => "denied" };
        if (q.Pane.Agent is { } a) q.Pane.Agent = a with { Status = "working", Since = DateTime.Now, Message = $"{verb} {q.Tool} from {who}" };
        q.Pane.Workspace.Logs.Add(new LogEntry(DateTime.Now, decision == "deny" ? "warning" : "success", $"{verb} {q.Tool} from {who}"));
        Finish(q, answer);
        return true;
    }

    /// <summary>The agent moved on (another hook event): its question was answered in the terminal.</summary>
    public void CancelFor(PaneBase pane)
    {
        foreach (var q in _pending.Where(p => p.Pane == pane).ToList()) Finish(q, null);
    }

    /// <summary>You're back at the desk: the terminal's own dialog takes over.</summary>
    public void CancelAll()
    {
        foreach (var q in _pending.ToList()) Finish(q, null);
    }

    private void Finish(PendingPrompt q, JsonObject? answer)
    {
        if (!_pending.Remove(q)) return;
        q.Answer.TrySetResult(answer);
        _mux.Events.Publish("prompt", "resolved", new JsonObject { ["id"] = q.Id, ["pane"] = q.Pane.Id, ["answered"] = answer != null });
        q.Pane.Workspace.Refresh();
    }

    /// <summary>One line for the card ("npm test", "src/app.ts") and optional detail for the expanded view.</summary>
    internal static (string Summary, string? Detail) Describe(string tool, JsonObject? input)
    {
        string? S(string key) => input?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        string Clip(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
        string OneLine(string s) => Clip(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(), 200);

        switch (tool)
        {
            case "Bash":
            case "PowerShell":
            {
                var cmd = S("command") ?? "";
                var why = S("description");
                return (OneLine(cmd), why != null || cmd.Length > 200 || cmd.Contains('\n') ? Clip((why != null ? why + "\n\n" : "") + cmd, 4000) : null);
            }
            case "Edit":
            case "MultiEdit":
            {
                var path = S("file_path") ?? "";
                var old = S("old_string");
                var neu = S("new_string");
                string? detail = old != null || neu != null ? Clip($"- {old}\n+ {neu}", 4000) : null;
                if (input?["edits"] is JsonArray edits)
                    detail = Clip(string.Join("\n\n", edits.OfType<JsonObject>().Select(e => $"- {(string?)e["old_string"]}\n+ {(string?)e["new_string"]}")), 4000);
                return (path, detail);
            }
            case "Write":
                return (S("file_path") ?? "", S("content") is { } content ? Clip(content, 4000) : null);
            case "NotebookEdit":
                return (S("notebook_path") ?? "", S("new_source") is { } src ? Clip(src, 4000) : null);
            case "Read":
                return (S("file_path") ?? "", null);
            case "WebFetch":
                return (S("url") ?? "", S("prompt"));
            case "WebSearch":
                return (S("query") ?? "", null);
            case "Task":
            case "Agent":
                return (S("description") ?? "", S("prompt") is { } p ? Clip(p, 4000) : null);
        }
        if (input == null || input.Count == 0) return ("", null);
        // MCP and other tools: the first short string field as the summary, all input as detail.
        var first = input.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s != null);
        var json = input.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return (first != null ? OneLine(first) : "", Clip(json, 4000));
    }
}
