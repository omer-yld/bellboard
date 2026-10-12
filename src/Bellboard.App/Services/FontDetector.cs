using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bellboard.Services;

/// <summary>
/// Picks the terminal font stack. If the user hasn't chosen one, use Windows Terminal's font, then
/// always append an installed Nerd Font so prompt icons (starship, oh-my-posh, p10k) render.
/// Fonts are matched per glyph by the browser, so fallbacks cost nothing when unused.
/// </summary>
public static class FontDetector
{
    public static string Resolve(string configured, string defaultStack)
    {
        HashSet<string> installed;
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            installed = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return configured;
        }

        var stack = SplitStack(configured);
        if (configured == defaultStack && WindowsTerminalFace() is { } wt)
        {
            // WT names like "JetBrainsMono Nerd Font" map to installed families "JetBrainsMono NFM"/"NF" (Nerd Fonts v3).
            var front = new List<string> { wt };
            var stem = wt.Replace(" Nerd Font Mono", "").Replace(" Nerd Font", "").Trim();
            front.AddRange(new[] { stem + " NFM", stem + " NF", stem + " Nerd Font Mono" }.Where(installed.Contains));
            stack.InsertRange(0, front);
        }

        if (!stack.Any(IsNerdFont))
        {
            var symbols = new[] { "Symbols Nerd Font Mono", "Symbols Nerd Font" }.FirstOrDefault(installed.Contains)
                          ?? installed.Where(f => f.EndsWith(" NFM") || f.EndsWith("Nerd Font Mono")).OrderBy(f => f.Length).FirstOrDefault()
                          ?? installed.Where(IsNerdFont).OrderBy(f => f.Length).FirstOrDefault();
            if (symbols != null)
            {
                var generic = stack.FindIndex(f => f is "monospace" or "serif" or "sans-serif");
                stack.Insert(generic < 0 ? stack.Count : generic, symbols);
            }
        }
        return string.Join(", ", stack.Distinct(StringComparer.OrdinalIgnoreCase).Select(Quote));
    }

    private static bool IsNerdFont(string f) =>
        f.Contains("Nerd Font", StringComparison.OrdinalIgnoreCase) || f.EndsWith(" NF") || f.EndsWith(" NFM") || f.EndsWith(" NFP");

    private static List<string> SplitStack(string css) =>
        css.Split(',').Select(f => f.Trim().Trim('\'', '"')).Where(f => f.Length > 0).ToList();

    private static string Quote(string f) => f is "monospace" or "serif" or "sans-serif" ? f : $"'{f}'";

    /// <summary>Font face from Windows Terminal's settings (profiles.defaults), if any.</summary>
    public static string? WindowsTerminalFace()
    {
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (var dir in Directory.GetDirectories(packages, "Microsoft.WindowsTerminal*"))
            {
                var file = Path.Combine(dir, "LocalState", "settings.json");
                if (!File.Exists(file)) continue;
                var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                static string? Face(JsonNode? p) => (string?)p?["font"]?["face"] ?? (string?)p?["fontFace"];
                var profiles = root?["profiles"];
                var list = (profiles?["list"] as JsonArray ?? new JsonArray()).ToList();
                var defaultGuid = (string?)root?["defaultProfile"];
                // defaults → the default profile → any profile with a font set.
                var face = Face(profiles?["defaults"])
                           ?? Face(list.FirstOrDefault(p => (string?)p?["guid"] == defaultGuid))
                           ?? list.Select(Face).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
                if (!string.IsNullOrWhiteSpace(face)) return face;
            }
        }
        catch (Exception ex)
        {
            Log.Error("reading Windows Terminal settings", ex);
        }
        return null;
    }
}
