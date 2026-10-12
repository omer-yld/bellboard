using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bellboard.Panes;

namespace Bellboard.Core;

/// <summary>Split-tree node. Leaves are panes; inner nodes are two-way splits.</summary>
public abstract class Node
{
    public SplitNode? Parent { get; set; }
}

public sealed class SplitNode : Node
{
    /// <summary>true = children side by side (split right); false = stacked (split down).</summary>
    public bool Horizontal { get; set; }
    public Node A { get; set; } = null!;
    public Node B { get; set; } = null!;
    public double Ratio { get; set; } = 0.5;
    /// <summary>The Grid currently showing this split (lets resize/equalize update sizes without a rebuild).</summary>
    public Grid? View { get; set; }

    public void ApplyRatio()
    {
        if (View == null) return;
        var defs = Horizontal
            ? View.ColumnDefinitions.Select(c => (DefinitionBase)c).ToList()
            : View.RowDefinitions.Select(r => (DefinitionBase)r).ToList();
        if (defs.Count != 3) return;
        if (Horizontal)
        {
            ((ColumnDefinition)defs[0]).Width = new GridLength(Ratio, GridUnitType.Star);
            ((ColumnDefinition)defs[2]).Width = new GridLength(1 - Ratio, GridUnitType.Star);
        }
        else
        {
            ((RowDefinition)defs[0]).Height = new GridLength(Ratio, GridUnitType.Star);
            ((RowDefinition)defs[2]).Height = new GridLength(1 - Ratio, GridUnitType.Star);
        }
    }
}

/// <summary>Something closed that Ctrl+Shift+Z can bring back.</summary>
public sealed record ClosedItem(string Kind, string Label, string? CustomName, LayoutSpec Layout, string? WorkspaceId, AgentTask? Agent, int Index);

/// <summary>A status pill set through the API (`bell status set build "passing" --color green`).</summary>
public sealed record StatusPill(string Key, string Text, string? Color)
{
    public Brush Fill => new SolidColorBrush(Palette.Resolve(Color, 0x26));
    public Brush Stroke => new SolidColorBrush(Palette.Resolve(Color, 0xFF));
    public string Label => Text;
}

public sealed record LogEntry(DateTime At, string Level, string Text)
{
    public string Glyph => Level switch { "success" => "✓", "warning" => "!", "error" => "✕", "progress" => "…", _ => "›" };
    public Brush Brush => new SolidColorBrush(Palette.Resolve(Level switch
    {
        "success" => "green", "warning" => "yellow", "error" => "red", "progress" => "blue", _ => "gray",
    }, 0xFF));
}

/// <summary>Named colours for pills and log levels (or any #RRGGBB).</summary>
public static class Palette
{
    public static Color Resolve(string? name, byte alpha)
    {
        var c = (name ?? "gray").ToLowerInvariant() switch
        {
            "green" or "success" => Color.FromRgb(0x3F, 0xB9, 0x50),
            "red" or "error" => Color.FromRgb(0xF0, 0x6F, 0x78),
            "yellow" or "warning" or "orange" => Color.FromRgb(0xF5, 0xA5, 0x24),
            "blue" or "info" => Color.FromRgb(0x4C, 0x8D, 0xFF),
            "purple" => Color.FromRgb(0xB0, 0x82, 0xF0),
            "cyan" => Color.FromRgb(0x56, 0xB6, 0xC2),
            var s when s.StartsWith('#') && s.Length == 7 => (Color)ColorConverter.ConvertFromString(s),
            _ => Color.FromRgb(0x8A, 0x90, 0x9B),
        };
        c.A = alpha;
        return c;
    }
}

/// <summary>A vertical tab in the sidebar: a split tree of panes plus the metadata shown under its name.</summary>
public sealed class Workspace : INotifyPropertyChanged
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Id { get; }
    public Node? Root { get; set; }
    public Grid Container { get; } = new();
    public PaneBase? FocusedPane { get; set; }
    /// <summary>When set, only this pane is shown (Ctrl+Shift+Enter); the others keep running.</summary>
    public PaneBase? ZoomedPane { get; set; }
    public string? CustomName { get; set; }
    public string? LastNotice { get; set; }
    public int Unread { get; set; }
    // ---- Set through the status API (bell status / progress / log) ----
    public List<StatusPill> Status { get; } = new();
    public double? ProgressValue { get; set; }
    public string? ProgressLabel { get; set; }
    public List<LogEntry> Logs { get; } = new();

    public bool HasStatus => Status.Count > 0;
    public List<StatusPill> StatusItems => Status.ToList();
    public bool HasProgressBar => ProgressValue != null;
    public double ProgressPercent => (ProgressValue ?? 0) * 100;
    public string ProgressText2 => ProgressLabel is { Length: > 0 } l ? $"{l}  {ProgressPercent:0}%" : $"{ProgressPercent:0}%";
    public LogEntry? LastLog => Logs.Count > 0 ? Logs[^1] : null;
    public bool HasLog => Logs.Count > 0;

    /// <summary>Set for workspaces created by `bell agent new` (worktree + branch + task).</summary>
    public AgentTask? AgentTask
    {
        get => _agentTask;
        set
        {
            _agentTask = value;
            // The sidebar lists agents separately; tell its live filter this one moved.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgent)));
        }
    }
    private AgentTask? _agentTask;
    public bool IsAgent => AgentTask != null;
    public string AgentTaskTip => AgentTask is { } t ? $"{t.Task}\n{t.Branch} ← {t.Base}\n{t.Worktree}" : "";

    /// <summary>When this workspace was last hidden (for unloading idle renderers).</summary>
    public DateTime HiddenSince { get; set; } = DateTime.Now;

    /// <summary>Typing in any terminal of this workspace goes to all of them (Ctrl+Shift+A).</summary>
    public bool Broadcast { get; set; }

    /// <summary>Muted: notifications still ring and badge, but no toast or taskbar flash.</summary>
    public bool Muted { get; set; }
    public string MuteMenuText => Muted ? "Unmute notifications" : "Mute notifications";
    /// <summary>Pinned: listed first in the sidebar, and not closed until unpinned.</summary>
    public bool Pinned { get; set; }
    public string PinMenuText => Pinned ? "Unpin workspace" : "Pin workspace";
    /// <summary>1-based position in the sidebar (Ctrl+1…9).</summary>
    public int Index { get; set; }
    public bool IsSelected { get; set; }

    // Inline rename state (bound by the sidebar template).
    public bool IsEditing { get; set; }
    public string EditName { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public Workspace(string id) => Id = id;

    /// <summary>Leaves of the split tree (tab groups), left-to-right / top-to-bottom.</summary>
    public IEnumerable<PaneStack> Stacks => Walk(Root);
    /// <summary>Every pane, including inactive tabs.</summary>
    public IEnumerable<PaneBase> Panes => Stacks.SelectMany(s => s.Tabs);
    public int PaneCount => Panes.Count();
    public int StackCount => Stacks.Count();
    public PaneBase? FindPane(string id) => Panes.FirstOrDefault(p => p.Id == id);

    private static IEnumerable<PaneStack> Walk(Node? n)
    {
        switch (n)
        {
            case PaneStack p:
                yield return p;
                break;
            case SplitNode s:
                foreach (var x in Walk(s.A)) yield return x;
                foreach (var x in Walk(s.B)) yield return x;
                break;
        }
    }

    private TerminalPane? PrimaryTerminal =>
        FocusedPane as TerminalPane ?? Panes.OfType<TerminalPane>().FirstOrDefault();

    public string? Cwd => PrimaryTerminal?.Cwd;
    public string? Branch => PrimaryTerminal?.GitBranch;
    public int[] Ports => Panes.SelectMany(p => p.Ports).Distinct().Order().ToArray();

    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName!;
            if (PrimaryTerminal?.DisplayTitle is { } t) return t;
            if (PrimaryTerminal?.Remote is { } host) return host;
            if (Cwd is { Length: > 0 } cwd)
            {
                var trimmed = cwd.TrimEnd('\\', '/');
                var name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? trimmed : name;
            }
            return FocusedPane?.DisplayTitle ?? "Workspace";
        }
    }

    // ---- Sidebar view model ----

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            // An SSH pane's folder and branch are on the remote machine; the local ones would mislead.
            if (PrimaryTerminal?.Remote is { } host) return "ssh " + host;
            if (Branch is { } b) parts.Add("⎇ " + b);
            if (Cwd is { } c) parts.Add(ShortPath(c));
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasSubtitle => Subtitle.Length > 0;

    // ---- Redesigned sidebar rows ----

    /// <summary>The shell tile (WSL, PS, CMD, GIT, SSH, WEB) for the pane this workspace shows.</summary>
    public ShellBadge Badge => ShellBadge.For(PrimaryTerminal ?? FocusedPane);

    /// <summary>"~/projects/api · main": the folder (Linux-style for WSL) and branch, or "ssh host".</summary>
    public string Meta
    {
        get
        {
            if (PrimaryTerminal?.Remote is { } host) return "ssh " + host;
            var path = Cwd is { } c ? DisplayPath(c) : "";
            return Branch is { } b ? (path.Length > 0 ? path + " · " + b : b) : path;
        }
    }

    public string PanesText => Panes.Count() is var n && n > 1 ? $"· {n} panes" : "";
    public bool HasPanesText => Panes.Count() > 1;

    /// <summary>A pulsing amber dot replaces the index while something here wants you.</summary>
    public bool ShowRing => HasUnread || HasAttention;
    public bool ShowIndexNoRing => !ShowRing && Index is >= 1 and <= 9;

    /// <summary>"working" | "waiting" | "done" | "idle", for agent rows.</summary>
    // Without a report we don't know whether it's working, so don't claim it; an agent workspace's agent was started,
    // though, so it's "running" (as the inbox says), not "idle".
    public string AgentState => Agent?.Status ?? (AgentTask != null ? "running" : "idle");
    public bool AgentWorking => AgentState == "working";
    public bool AgentWaiting => AgentState == "waiting";
    public bool AgentDone => AgentState == "done";
    public bool AgentIdle => AgentState is not ("working" or "waiting" or "done");
    public string AgentStateText => AgentState switch
    {
        "working" => "Working",
        "waiting" => "Waiting for you",
        "done" => "Done",
        "running" => "Running",
        _ => "Idle",
    };
    public string AgentWho => AgentManager.DisplayName(Agent?.Kind ?? AgentTask?.Agent ?? "agent");
    public string AgentAge
    {
        get
        {
            var since = AgentTask?.Created ?? DateTime.Now;
            var m = (int)(DateTime.Now - since).TotalMinutes;
            return m < 1 ? "now" : m < 60 ? $"{m}m" : m < 48 * 60 ? $"{m / 60}h" : $"{m / 1440}d";
        }
    }
    public string AgentLine => $"{AgentWho} · {AgentStateText} · {AgentAge}";

    /// <summary>The agent status line on a regular workspace that happens to run an agent.</summary>
    public bool HasAgentLine => !IsAgent && HasAgent;

    /// <summary>"~/projects/api" for a WSL folder, "~\code\api" under the Windows home, else the path.</summary>
    public static string DisplayPath(string path)
    {
        if (Bellboard.Terminal.ShellResolver.ParseWslPath(path) is { } wsl)
        {
            var linux = System.Text.RegularExpressions.Regex.Replace(wsl.LinuxPath, "^/home/[^/]+", "~");
            return linux.Length > 40 ? "…" + linux[^39..] : linux;
        }
        return ShortPath(path);
    }
    public string PortsText => string.Join("  ", Ports.Select(p => ":" + p));
    public bool HasPorts => Ports.Length > 0;
    public bool HasNotice => !string.IsNullOrEmpty(LastNotice);
    public bool HasUnread => Unread > 0;
    public string IndexText => Index is >= 1 and <= 9 ? Index.ToString() : "";
    public bool ShowIndex => !HasUnread && Index is >= 1 and <= 9;
    public bool NotEditing => !IsEditing;
    public bool HasAttention => Panes.Any(p => p.Attention);

    private AgentInfo? Agent => Panes.OfType<TerminalPane>().Select(t => t.Agent).FirstOrDefault(a => a != null);
    public bool HasAgent => Agent != null || ProgressText != null;

    public string AgentText
    {
        get
        {
            var parts = new List<string>();
            if (Agent is { } a)
            {
                var who = AgentManager.DisplayName(a.Kind);
                parts.Add(a.Status switch
                {
                    "working" => $"{who} · working",
                    "waiting" => $"{who} · needs input",
                    "done" => $"{who} · done",
                    _ => $"{who} · idle",
                });
            }
            if (ProgressText is { } p) parts.Add(p);
            return string.Join("  ", parts);
        }
    }

    /// <summary>"2 subagents" while Claude Code runs subagents in this workspace.</summary>
    public string SubagentsText => Panes.OfType<TerminalPane>().Sum(t => t.Subagents.Count) is var n && n > 0 ? $"{n} subagent{(n == 1 ? "" : "s")}" : "";
    public bool HasSubagents => SubagentsText.Length > 0;
    public bool HasPills => HasSubagents || HasStatus;

    public Brush AgentBrush => Agent?.Status switch
    {
        "working" => new SolidColorBrush(PaneBase.BlueColor),
        "waiting" => new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)),
        "done" => new SolidColorBrush(Color.FromRgb(0x5F, 0xD3, 0x9A)),
        _ => new SolidColorBrush(Color.FromRgb(0x6C, 0x73, 0x84)),
    };

    private string? ProgressText
    {
        get
        {
            var p = Panes.OfType<TerminalPane>().Select(t => t.Progress).FirstOrDefault(x => x != null);
            if (p is not { } v) return null;
            return v.State switch
            {
                2 => "▲ error",
                3 => "▰ working…",
                4 => $"▰ {v.Percent}% (paused)",
                _ => $"▰ {v.Percent}%",
            };
        }
    }

    public static string ShortPath(string path)
    {
        if (Bellboard.Terminal.ShellResolver.ParseWslPath(path) is { } wsl)
        {
            var linux = System.Text.RegularExpressions.Regex.Replace(wsl.LinuxPath, "^/home/[^/]+", "~");
            return "WSL " + (linux.Length > 40 ? "…" + linux[^39..] : linux);
        }
        if (path.StartsWith(Home, StringComparison.OrdinalIgnoreCase))
            path = "~" + path[Home.Length..];
        if (path.Length > 42)
        {
            var parts = path.Split('\\', '/');
            if (parts.Length > 3) path = parts[0] + "\\…\\" + string.Join("\\", parts[^2..]);
        }
        return path;
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var stack in Stacks) stack.RefreshHeader();
        if (IsSelected) Refreshed?.Invoke(this);
    }

    /// <summary>Raised after a refresh of the selected workspace (drives the window title).</summary>
    public static event Action<Workspace>? Refreshed;
}
