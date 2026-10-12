using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Bellboard.Core;

/// <summary>
/// A workspace layout as plain data: what the session file stores and what layout templates and
/// "reopen closed" replay. Converting to/from live panes happens in SessionStore.
/// </summary>
public abstract record LayoutSpec;

public sealed record SplitSpec(bool Horizontal, double Ratio, LayoutSpec A, LayoutSpec B) : LayoutSpec;

/// <summary>A pane position holding one or more tabs; Active is the visible tab.</summary>
public sealed record StackSpec(IReadOnlyList<LayoutSpec> Tabs, int Active) : LayoutSpec;

public sealed record TerminalSpec(string? Cwd, string? Scrollback = null, string? AgentKind = null, string? AgentSessionId = null, string? Command = null, string? Shell = null,
    bool Pinned = false) : LayoutSpec;

public sealed record BrowserSpec(string? Url, bool Pinned = false) : LayoutSpec;

public static class LayoutJson
{
    private const int MaxDepth = 32;

    public static JsonObject ToJson(LayoutSpec spec) => spec switch
    {
        SplitSpec s => new JsonObject
        {
            ["type"] = "split",
            ["horizontal"] = s.Horizontal,
            ["ratio"] = Math.Round(s.Ratio, 4),
            ["a"] = ToJson(s.A),
            ["b"] = ToJson(s.B),
        },
        StackSpec st => new JsonObject
        {
            ["type"] = "stack",
            ["active"] = st.Active,
            ["tabs"] = new JsonArray(st.Tabs.Select(t => (JsonNode)ToJson(t)).ToArray()),
        },
        TerminalSpec t => Terminal(t),
        BrowserSpec b => new JsonObject { ["type"] = "browser", ["url"] = b.Url, ["pinned"] = b.Pinned ? true : null },
        _ => throw new ArgumentException("unknown layout node"),
    };

    private static JsonObject Terminal(TerminalSpec t)
    {
        var o = new JsonObject { ["type"] = "terminal", ["cwd"] = t.Cwd };
        if (t.Scrollback != null) o["scrollback"] = t.Scrollback;
        if (t.Command != null) o["command"] = t.Command;
        if (t.Shell != null) o["shell"] = t.Shell;
        if (t.Pinned) o["pinned"] = true;
        if (t.AgentSessionId != null) o["agent"] = new JsonObject { ["kind"] = t.AgentKind ?? "claude", ["sessionId"] = t.AgentSessionId };
        return o;
    }

    /// <summary>Lenient parse: anything unrecognised becomes a plain terminal; ratios are clamped.</summary>
    public static LayoutSpec FromJson(JsonObject? o) => Parse(o, 0);

    private static LayoutSpec Parse(JsonObject? o, int depth)
    {
        if (depth > MaxDepth) return new TerminalSpec(null);
        switch ((string?)o?["type"])
        {
            case "split":
                return new SplitSpec(
                    (bool?)o!["horizontal"] ?? true,
                    Math.Clamp((double?)o["ratio"] ?? 0.5, 0.1, 0.9),
                    Parse(o["a"] as JsonObject, depth + 1),
                    Parse(o["b"] as JsonObject, depth + 1));
            case "stack":
                var tabs = (o!["tabs"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(t => Parse(t, depth + 1))
                    .Where(t => t is TerminalSpec or BrowserSpec)
                    .ToList();
                if (tabs.Count == 0) return new TerminalSpec(null);
                if (tabs.Count == 1) return tabs[0];
                return new StackSpec(tabs, Math.Clamp((int?)o["active"] ?? 0, 0, tabs.Count - 1));
            case "browser":
                return new BrowserSpec((string?)o!["url"], (bool?)o["pinned"] == true);
            default:
                var agent = o?["agent"] as JsonObject;
                return new TerminalSpec(
                    (string?)o?["cwd"],
                    (string?)o?["scrollback"],
                    agent == null ? null : (string?)agent["kind"] ?? "claude",
                    (string?)agent?["sessionId"],
                    (string?)o?["command"],
                    (string?)o?["shell"],
                    (bool?)o?["pinned"] == true);
        }
    }
}
