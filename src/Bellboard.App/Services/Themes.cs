using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bellboard.Services;

/// <summary>A terminal colour scheme in xterm.js ITheme keys.</summary>
public sealed record ThemePreset(string Name, string Source, Dictionary<string, string> Colors);

/// <summary>Built-in presets plus the user's Windows Terminal schemes.</summary>
public static class Themes
{
    private static readonly string[] Ansi = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];

    public static List<ThemePreset> All()
    {
        var list = new List<ThemePreset> { new("Bellboard Dark", "Bellboard", BellboardConfig.DefaultTheme()) };
        list.AddRange(BuiltIn.Select(b => new ThemePreset(b.Name, "Bellboard", FromWindowsTerminal(b.Scheme))));
        list.AddRange(WindowsTerminalSchemes().Where(s => list.All(x => !x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))));
        return list;
    }

    /// <summary>Converts a Windows Terminal scheme ("purple", "cursorColor", …) to xterm.js theme keys.</summary>
    public static Dictionary<string, string> FromWindowsTerminal(JsonObject scheme)
    {
        string? Get(string k) => (string?)scheme[k];
        var t = new Dictionary<string, string>();
        void Set(string key, string? value) { if (!string.IsNullOrEmpty(value)) t[key] = value!; }
        Set("background", Get("background"));
        Set("foreground", Get("foreground"));
        Set("cursor", Get("cursorColor") ?? Get("foreground"));
        Set("cursorAccent", Get("background"));
        Set("selectionBackground", Get("selectionBackground"));
        foreach (var a in Ansi)
        {
            var wt = a == "magenta" ? "purple" : a;
            Set(a, Get(wt));
            Set("bright" + char.ToUpperInvariant(a[0]) + a[1..], Get("bright" + char.ToUpperInvariant(wt[0]) + wt[1..]));
        }
        // Fill anything missing from the default so xterm always gets a complete theme.
        foreach (var kv in BellboardConfig.DefaultTheme()) t.TryAdd(kv.Key, kv.Value);
        return t;
    }

    public static List<ThemePreset> WindowsTerminalSchemes()
    {
        var result = new List<ThemePreset>();
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return result;
            foreach (var dir in Directory.GetDirectories(packages, "Microsoft.WindowsTerminal*"))
            {
                var file = Path.Combine(dir, "LocalState", "settings.json");
                if (!File.Exists(file)) continue;
                var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var s in (root?["schemes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if ((string?)s["name"] is { } name && s["background"] != null)
                        result.Add(new ThemePreset(name, "Windows Terminal", FromWindowsTerminal(s)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("reading Windows Terminal schemes", ex);
        }
        return result;
    }

    private static JsonObject Scheme(string bg, string fg, string sel, params string[] ansi16)
    {
        var o = new JsonObject { ["background"] = bg, ["foreground"] = fg, ["selectionBackground"] = sel, ["cursorColor"] = fg };
        string[] keys = ["black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
                         "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite"];
        for (int i = 0; i < keys.Length && i < ansi16.Length; i++) o[keys[i]] = ansi16[i];
        return o;
    }

    private static readonly (string Name, JsonObject Scheme)[] BuiltIn =
    [
        ("One Half Dark", Scheme("#282C34", "#DCDFE4", "#474E5D",
            "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
            "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4")),
        ("Campbell", Scheme("#0C0C0C", "#CCCCCC", "#3A3A3A",
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2")),
        ("Tokyo Night", Scheme("#1A1B26", "#C0CAF5", "#33467C",
            "#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
            "#414868", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5")),
        ("Dracula", Scheme("#282A36", "#F8F8F2", "#44475A",
            "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
            "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF")),
        ("Solarized Dark", Scheme("#002B36", "#839496", "#073642",
            "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
            "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3")),
        ("GitHub Light", Scheme("#FFFFFF", "#24292F", "#C8D9F0",
            "#24292F", "#CF222E", "#116329", "#4D2D00", "#0969DA", "#8250DF", "#1B7C83", "#6E7781",
            "#57606A", "#A40E26", "#1A7F37", "#633C01", "#218BFF", "#A475F9", "#3192AA", "#8C959F")),
    ];
}
