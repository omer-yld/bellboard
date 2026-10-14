using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bellboard.Core;
using Bellboard.Panes;
using Bellboard.Services;
using Bellboard.Shared;

namespace Bellboard.Ipc;

/// <summary>
/// The JSON shapes of Bellboard's state, shared by `bell list`, the inbox API and the remote phone view
/// (src/Bellboard.Remote/PROTOCOL.md describes them).
/// </summary>
public static class StateJson
{
    public static JsonObject Workspace(Workspace ws, MuxController mux) => new()
    {
        ["id"] = ws.Id,
        ["index"] = ws.Index,
        ["name"] = ws.Name,
        ["selected"] = ws == mux.Selected,
        ["cwd"] = ws.Cwd,
        ["branch"] = ws.Branch,
        ["ports"] = JsonSerializer.SerializeToNode(ws.Ports),
        ["unread"] = ws.Unread,
        ["notice"] = ws.LastNotice,
        ["muted"] = ws.Muted,
        ["pinned"] = ws.Pinned,
        ["status"] = ws.HasAgent ? ws.AgentText : null,
        ["panes"] = new JsonArray(ws.Panes.Select(pn => JsonSerializer.SerializeToNode(pn.Describe())).ToArray()),
    };

    /// <summary>A workspace as the phone shows it: the sidebar row plus its pills, progress and latest logs.</summary>
    public static JsonObject RemoteWorkspace(Workspace ws, MuxController mux)
    {
        var o = Workspace(ws, mux);
        o["badge"] = ws.Badge.Text;
        o["agentTask"] = ws.AgentTask is { } t
            ? new JsonObject { ["task"] = t.Task, ["agent"] = t.Agent, ["branch"] = t.Branch, ["base"] = t.Base }
            : null;
        o["pills"] = new JsonArray(ws.Status.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["text"] = s.Text, ["color"] = s.Color }).ToArray());
        o["progress"] = ws.ProgressValue is { } v ? new JsonObject { ["value"] = v, ["label"] = ws.ProgressLabel } : null;
        o["logs"] = new JsonArray(ws.Logs.TakeLast(5)
            .Select(l => (JsonNode)new JsonObject { ["at"] = l.At.ToString("o"), ["level"] = l.Level, ["text"] = l.Text }).ToArray());
        o["panes"] = new JsonArray(ws.Panes.Select(p => (JsonNode)RemotePane(p)).ToArray());
        return o;
    }

    private static JsonObject RemotePane(PaneBase p)
    {
        var t = p as TerminalPane;
        return new JsonObject
        {
            ["id"] = p.Id,
            ["kind"] = p.Kind,
            ["title"] = p.DisplayTitle,
            ["cwd"] = p.Cwd,
            ["focused"] = p.Workspace.FocusedPane == p,
            ["active"] = p.Stack?.Active == p,
            ["attention"] = p.Attention,
            ["agent"] = t?.Agent is { } a ? new JsonObject { ["kind"] = a.Kind, ["status"] = a.Status } : null,
            ["feed"] = t?.Feed ?? false,
            ["shell"] = t == null ? null : t.Feed ? "feed" : t.ShellKind.ToString().ToLowerInvariant(),
            ["size"] = t?.SizeText,
            ["branch"] = t?.GitBranch,
        };
    }

    public static JsonArray Inbox(MuxController mux) => new(Services.Inbox.Build(mux).Select(r => (JsonNode)new JsonObject
    {
        ["pane"] = r.Pane.Id, ["workspace"] = r.Pane.Workspace.Id, ["title"] = r.Title, ["status"] = r.Status,
        ["since"] = r.Since == default ? null : r.Since.ToString("o"), ["message"] = r.Message, ["where"] = r.Where,
        ["feed"] = r.Pane is TerminalPane { Feed: true },
        ["agent"] = r.Pane is TerminalPane { Agent: { } running } ? AgentManager.DisplayName(running.Kind)
            : r.Pane.Workspace.AgentTask is { } task ? AgentManager.DisplayName(task.Agent) : null,
        ["workspaceName"] = r.Pane.Workspace.Name,
        ["prompt"] = mux.Prompts.For(r.Pane) is { } q ? Prompt(q) : null,
    }).ToArray());

    public static JsonObject Prompt(PendingPrompt q) => new()
    {
        ["id"] = q.Id,
        ["tool"] = q.Tool,
        ["summary"] = q.Summary,
        ["detail"] = q.Detail,
        ["canAlwaysAllow"] = q.CanAlwaysAllow,
        ["since"] = q.Since.ToString("o"),
    };

    public static JsonObject Notice(Notice n) => new()
    {
        ["id"] = n.Id,
        ["at"] = n.At.ToString("o"),
        ["workspace"] = n.Workspace.Id,
        ["workspaceName"] = n.Workspace.Name,
        ["pane"] = n.Pane.Id,
        ["title"] = n.Title,
        ["body"] = n.Body,
        ["source"] = n.Source,
        ["read"] = n.Read,
    };

    /// <summary>Everything the phone needs for its screens (the sidecar adds "device").</summary>
    public static JsonObject Remote(MuxController mux) => new()
    {
        ["version"] = BellboardProtocol.Version,
        // The PC's clock, so the phone can say "waiting 4m" right even if its own clock is off.
        ["now"] = System.DateTime.Now.ToString("o"),
        ["dnd"] = mux.Notifications.DoNotDisturb,
        ["unread"] = mux.Notifications.UnreadCount,
        ["workspaces"] = new JsonArray(mux.Workspaces.Select(w => (JsonNode)RemoteWorkspace(w, mux)).ToArray()),
        ["inbox"] = Inbox(mux),
        ["notifications"] = new JsonArray(mux.Notifications.Items.Where(n => mux.Workspaces.Contains(n.Workspace))
            .Take(50).Select(n => (JsonNode)Notice(n)).ToArray()),
    };
}
