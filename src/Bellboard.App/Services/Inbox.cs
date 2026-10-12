using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Bellboard.Core;
using Bellboard.Panes;

namespace Bellboard.Services;

/// <summary>One agent in the inbox: who, what state, since when, and what it last said.</summary>
public sealed class AgentRow
{
    public required string Title { get; init; }
    public required string Where { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }
    public DateTime Since { get; init; }
    public required PaneBase Pane { get; init; }

    public string StatusText => Status switch
    {
        "waiting" => "needs input",
        "working" => "working",
        "done" => "done",
        "idle" => "idle",
        _ => "running",
    } + (Since == default ? "" : " · " + Ago(Since));
    public Brush Dot => new SolidColorBrush(Status switch
    {
        "waiting" => Color.FromRgb(0xF5, 0xA5, 0x24),
        "working" => PaneBase.BlueColor,
        "done" => Color.FromRgb(0x5F, 0xD3, 0x9A),
        _ => Color.FromRgb(0x7F, 0x85, 0x90),
    });
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool CanApprove => Status == "waiting";
    /// <summary>Sort: needs input first (longest waiting first), then working, done, idle.</summary>
    public int Rank => Status switch { "waiting" => 0, "working" => 1, "done" => 2, _ => 3 };

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h {(int)d.Minutes}m";
        return $"{(int)d.TotalDays}d";
    }
}

public static class Inbox
{
    /// <summary>Agent panes known from hooks, plus agent workspaces whose agent hasn't reported yet.</summary>
    public static List<AgentRow> Build(MuxController mux)
    {
        var rows = new List<AgentRow>();
        foreach (var ws in mux.Workspaces)
        {
            var reported = false;
            foreach (var t in ws.Panes.OfType<TerminalPane>().Where(t => t.Agent != null))
            {
                reported = true;
                var a = t.Agent!;
                var who = AgentManager.DisplayName(a.Kind);
                var subs = t.Subagents.Values.Select(s => s.Type).ToList();
                rows.Add(new AgentRow
                {
                    Title = ws.AgentTask?.Task is { } task ? task : $"{who} in {ws.Name}",
                    Where = $"{who} · {ws.Name}{(ws.Branch is { } b ? " · ⎇ " + b : "")} · {t.Id}"
                            + (subs.Count > 0 ? $" · subagents: {string.Join(", ", subs)}" : ""),
                    Status = a.Status,
                    Message = a.Message,
                    Since = a.Since,
                    Pane = t,
                });
            }
            if (!reported && ws.AgentTask is { } at && ws.FocusedPane is { } pane)
            {
                rows.Add(new AgentRow
                {
                    Title = at.Task,
                    Where = $"{(at.Agent.Contains(' ') ? "custom agent" : at.Agent)} · {ws.Name} · ⎇ {at.Branch}",
                    Status = "running",
                    Message = ws.LastLog?.Text ?? ws.LastNotice,
                    Since = at.Created,
                    Pane = pane,
                });
            }
        }
        return rows.OrderBy(r => r.Rank).ThenBy(r => r.Since).ToList();
    }
}
