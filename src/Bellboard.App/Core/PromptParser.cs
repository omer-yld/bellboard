using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bellboard.Core;

public sealed record PromptChoice(string Key, string Label, bool Selected);

/// <summary>A numbered menu an agent shows in its terminal ("Do you want to proceed? ❯ 1. Yes  2. … 3. No").</summary>
public sealed record ScreenPrompt(string? Question, List<PromptChoice> Choices);

/// <summary>
/// Finds the choice menu on an agent's screen, so a phone can answer it with buttons instead of keystrokes.
/// Works on the plain text of the last lines (`terminal.read`): Claude Code, Codex and Pi all draw their
/// approval menus as numbered lines with a pointer (❯ › ▶ >) on the highlighted one, sometimes inside a box.
/// </summary>
public static class PromptParser
{
    // Optional box edge, optional pointer, "1." or "1)", the label, optional box edge.
    private static readonly Regex ChoiceLine = new(
        @"^\s*[│┃|]?\s*(?<ptr>[❯›▶►>→➜])?\s*(?<n>[1-9])[.)]\s+(?<label>\S.*?)\s*[│┃|]?\s*$", RegexOptions.Compiled);

    private static readonly char[] BoxChars = ['│', '┃', '|', '╭', '╮', '╰', '╯', '─', '━', '┌', '┐', '└', '┘'];

    public static ScreenPrompt? Parse(string screen)
    {
        var lines = screen.Replace("\r", "").Split('\n');
        // The menu nearest the bottom wins: scan upward for the last run 1..n.
        for (int end = lines.Length - 1; end >= 0; end--)
        {
            var m = ChoiceLine.Match(lines[end]);
            if (!m.Success) continue;
            int last = int.Parse(m.Groups["n"].Value);
            if (last < 2) continue;
            // Walk up collecting n-1 … 1, allowing wrapped label lines in between.
            var found = new SortedDictionary<int, (string Label, bool Selected)>();
            int expect = last, i = end, firstLine = end;
            while (i >= 0 && expect >= 1 && end - i < 40)
            {
                var c = ChoiceLine.Match(lines[i]);
                if (c.Success && int.Parse(c.Groups["n"].Value) == expect)
                {
                    found[expect] = (Clean(c.Groups["label"].Value), c.Groups["ptr"].Success);
                    firstLine = i;
                    expect--;
                }
                else if (c.Success) break;
                i--;
            }
            if (expect != 0) continue;
            var choices = found.Select(kv => new PromptChoice(kv.Key.ToString(), kv.Value.Label, kv.Value.Selected)).ToList();
            return new ScreenPrompt(Question(lines, firstLine), choices);
        }
        return null;
    }

    private static string? Question(string[] lines, int firstChoice)
    {
        for (int i = firstChoice - 1; i >= 0 && firstChoice - i <= 6; i--)
        {
            var t = Clean(lines[i]);
            if (t.Length == 0) continue;
            return t;
        }
        return null;
    }

    private static string Clean(string s) => s.Trim().Trim(BoxChars).Trim();

    /// <summary>A short fingerprint of what the phone saw, so an answer is only typed into the same screen.</summary>
    public static string Hash(string screen)
    {
        var normalized = string.Join('\n', screen.Replace("\r", "").Split('\n').Select(l => l.TrimEnd())).TrimEnd();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }
}
