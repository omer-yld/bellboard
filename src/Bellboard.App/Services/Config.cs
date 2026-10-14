using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bellboard.Shared;

namespace Bellboard.Services;

internal static class Log
{
    private static readonly object Gate = new();
    /// <summary>%LOCALAPPDATA%\Bellboard, or BELLBOARD_HOME for an isolated (dev/test) instance.</summary>
    public static string Dir { get; } = BellboardProtocol.LocalDataDir;
    private static string FilePath => Path.Combine(Dir, "bellboard.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>%APPDATA%\Bellboard\config.json — created with defaults on first run.</summary>
public sealed class BellboardConfig
{
    /// <summary>Shell command line. null = pwsh if installed, else Windows PowerShell. e.g. "cmd.exe", "wsl.exe", "C:\\Program Files\\Git\\bin\\bash.exe -l"</summary>
    public string? Shell { get; set; }
    public const string DefaultFontStack = "'Cascadia Mono', 'Cascadia Code', Consolas, 'Courier New', monospace";
    /// <summary>CSS font stack. Left at the default, Bellboard uses Windows Terminal's font and adds a Nerd Font for icons.</summary>
    public string FontFamily { get; set; } = DefaultFontStack;
    public double FontSize { get; set; } = 14;
    public double LineHeight { get; set; } = 1.12;
    /// <summary>"block", "bar" or "underline".</summary>
    public string CursorStyle { get; set; } = "block";
    public bool CursorBlink { get; set; } = true;
    public int Scrollback { get; set; } = 10000;
    public bool RestoreSession { get; set; } = true;
    /// <summary>Re-run "claude --resume &lt;id&gt;" in panes that had a Claude Code session.</summary>
    public bool RestoreAgents { get; set; } = true;
    public bool DesktopNotifications { get; set; } = true;
    /// <summary>A BEL in a background pane lights its ring.</summary>
    public bool BellIsAttention { get; set; } = true;
    /// <summary>"bundled" (conpty.dll + OpenConsole.exe next to the app) or "system".</summary>
    public string Conpty { get; set; } = "bundled";
    /// <summary>Where new browser panes start. null = the Bellboard start page.</summary>
    public string? HomeUrl { get; set; }
    /// <summary>Search used by the address bar and start page; %s is replaced by the query.</summary>
    public string SearchUrl { get; set; } = "https://www.google.com/search?q=%s";
    /// <summary>Keep one shell started for the next new workspace (instant prompt; costs one idle shell, ~70 MB for PowerShell).</summary>
    public bool PrestartShell { get; set; } = true;
    /// <summary>Free the renderers of workspaces hidden this long (shells keep running; 0 = never).</summary>
    public int UnloadIdleWorkspacesMinutes { get; set; } = 30;
    /// <summary>"inherit" (the focused terminal's folder), "home", or a folder path.</summary>
    public string NewWorkspaceDirectory { get; set; } = "inherit";
    public Dictionary<string, string> Theme { get; set; } = DefaultTheme();
    /// <summary>Bellboard Remote: the phone view (Settings › Remote).</summary>
    public RemoteConfig Remote { get; set; } = new();

    public static string Dir { get; } = BellboardProtocol.RoamingDataDir;
    public static string FilePath => Path.Combine(Dir, "config.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>URL (or path) of the latest.json written by `build.ps1 -Package`; Bellboard checks it daily. null = never.</summary>
    public string? UpdateFeed { get; set; }

    /// <summary>Name of the theme preset last chosen in Settings (informational; Theme holds the colours).</summary>
    public string? ThemeName { get; set; } = "Bellboard Dark";

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Copy every setting from another instance (keeps references to this one valid).</summary>
    public void CopyFrom(BellboardConfig other)
    {
        foreach (var prop in typeof(BellboardConfig).GetProperties())
            if (prop.CanWrite && prop.CanRead) prop.SetValue(this, prop.GetValue(other));
    }

    public BellboardConfig Clone()
    {
        var c = JsonSerializer.Deserialize<BellboardConfig>(JsonSerializer.Serialize(this, Json), Json)!;
        return c;
    }

    public static BellboardConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<BellboardConfig>(File.ReadAllText(FilePath), Json) ?? new BellboardConfig();
                if (IsOldDefaultTheme(cfg.Theme, cfg.ThemeName))
                {
                    cfg.Theme = DefaultTheme();
                    cfg.ThemeName = "Bellboard Dark";
                }
                foreach (var kv in DefaultTheme()) cfg.Theme.TryAdd(kv.Key, kv.Value);
                cfg.Remote ??= new RemoteConfig();
                // 0.1.0 wrote Google as the default home page; the start page replaces it.
                if (cfg.HomeUrl == "https://www.google.com") cfg.HomeUrl = null;
                return cfg;
            }
            var fresh = new BellboardConfig();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, Json));
            return fresh;
        }
        catch (Exception ex)
        {
            Log.Error("config load failed, using defaults", ex);
            return new BellboardConfig();
        }
    }

    /// <summary>Bellboard Dark (0.4 palette).</summary>
    public static Dictionary<string, string> DefaultTheme() => new()
    {
        ["background"] = "#14161b",
        ["foreground"] = "#d7dbe3",
        ["cursor"] = "#e8eaf0",
        ["cursorAccent"] = "#14161b",
        ["selectionBackground"] = "#3a3424",
        ["black"] = "#22262f",
        ["red"] = "#ef6b73",
        ["green"] = "#9fd07f",
        ["yellow"] = "#ebc46b",
        ["blue"] = "#72b4ff",
        ["magenta"] = "#c99bf0",
        ["cyan"] = "#62c7c2",
        ["white"] = "#e4e7ee",
        ["brightBlack"] = "#646b7a",
        ["brightRed"] = "#ff8a91",
        ["brightGreen"] = "#b5e098",
        ["brightYellow"] = "#f5d68a",
        ["brightBlue"] = "#92c5ff",
        ["brightMagenta"] = "#dab5f7",
        ["brightCyan"] = "#7fd8d3",
        ["brightWhite"] = "#f4f5f8",
    };

    /// <summary>
    /// Whether these colours are the untouched pre-0.4 Bellboard Dark (or wmux Dark), which upgrades to the new
    /// palette on load; colours anyone picked or edited stay as they are.
    /// </summary>
    internal static bool IsOldDefaultTheme(Dictionary<string, string> theme, string? name) =>
        name is null or "Bellboard Dark" or "wmux Dark"
        && theme.TryGetValue("background", out var bg) && bg.Equals("#15171c", StringComparison.OrdinalIgnoreCase)
        && theme.TryGetValue("foreground", out var fg) && fg.Equals("#d7dae0", StringComparison.OrdinalIgnoreCase)
        && theme.TryGetValue("blue", out var blue) && blue.Equals("#61afef", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bellboard Remote settings ("remote" in config.json).</summary>
public sealed class RemoteConfig
{
    /// <summary>Run the remote sidecar (bellboard-remote.exe). Off by default: nothing listens until you turn it on.</summary>
    public bool Enabled { get; set; }
    /// <summary>"tailscale" (127.0.0.1, published with `tailscale serve`), "local" (127.0.0.1, your own tunnel) or "lan" (HTTPS on every interface).</summary>
    public string Mode { get; set; } = "tailscale";
    public int Port { get; set; } = 17821;
    /// <summary>The address phones open, e.g. https://pc.tailnet.ts.net (filled in by the Tailscale setup).</summary>
    public string? Url { get; set; }
    /// <summary>Web Push notifications to paired phones when they aren't looking.</summary>
    public bool Push { get; set; } = true;
    /// <summary>Put the agent's message in push notifications (off: "Needs your input").</summary>
    public bool PushText { get; set; }
    /// <summary>Claude Code permission requests go to the phone: "away" (when you're not at the desk), "always" or "off".</summary>
    public string AnswerPrompts { get; set; } = "away";
    /// <summary>Minutes without Bellboard in the foreground before you count as away (a locked screen counts at once).</summary>
    public int AwayMinutes { get; set; } = 5;
}

