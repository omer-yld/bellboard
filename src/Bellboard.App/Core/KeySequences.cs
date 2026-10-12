using System;
using System.Collections.Generic;
using System.Linq;

namespace Bellboard.Core;

/// <summary>
/// Key names → the bytes a terminal sends ("enter", "ctrl+c", "shift+tab", "alt+x", "ctrl+left", "f5").
/// Used by `bell send-key` and the MCP server so scripts can drive TUIs.
/// </summary>
public static class KeySequences
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = "\r", ["return"] = "\r", ["tab"] = "\t", ["esc"] = "\x1b", ["escape"] = "\x1b",
        ["backspace"] = "\x7f", ["bs"] = "\x7f", ["space"] = " ",
        ["delete"] = "\x1b[3~", ["del"] = "\x1b[3~", ["insert"] = "\x1b[2~",
        ["pageup"] = "\x1b[5~", ["pgup"] = "\x1b[5~", ["pagedown"] = "\x1b[6~", ["pgdn"] = "\x1b[6~",
        ["f1"] = "\x1bOP", ["f2"] = "\x1bOQ", ["f3"] = "\x1bOR", ["f4"] = "\x1bOS",
        ["f5"] = "\x1b[15~", ["f6"] = "\x1b[17~", ["f7"] = "\x1b[18~", ["f8"] = "\x1b[19~",
        ["f9"] = "\x1b[20~", ["f10"] = "\x1b[21~", ["f11"] = "\x1b[23~", ["f12"] = "\x1b[24~",
    };

    // CSI final bytes for keys that take a modifier parameter (ESC [ 1 ; mod X).
    private static readonly Dictionary<string, char> Cursor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = 'A', ["down"] = 'B', ["right"] = 'C', ["left"] = 'D', ["home"] = 'H', ["end"] = 'F',
    };

    /// <summary>Translate one key chord. Throws ArgumentException for unknown names.</summary>
    public static string ToSequence(string chord)
    {
        if (string.IsNullOrEmpty(chord)) throw new ArgumentException("empty key");
        if (chord.Length == 1) return chord;
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        if (chord.EndsWith("++")) parts.Add("+");
        var key = parts[^1];
        bool ctrl = false, alt = false, shift = false;
        foreach (var m in parts.Take(parts.Count - 1))
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt" or "meta" or "option": alt = true; break;
                case "shift": shift = true; break;
                default: throw new ArgumentException($"unknown modifier '{m}' in '{chord}'");
            }
        }

        if (Cursor.TryGetValue(key, out var final))
        {
            int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            return mod == 1 ? $"\x1b[{final}" : $"\x1b[1;{mod}{final}";
        }
        if (key.Equals("tab", StringComparison.OrdinalIgnoreCase) && shift && !ctrl) return (alt ? "\x1b" : "") + "\x1b[Z";

        string seq;
        if (Named.TryGetValue(key, out var named))
        {
            seq = named;
            if (ctrl && key.Equals("space", StringComparison.OrdinalIgnoreCase)) seq = "\0";
            else if (ctrl) throw new ArgumentException($"ctrl+{key} has no terminal encoding");
        }
        else if (key.Length == 1)
        {
            char c = shift ? char.ToUpperInvariant(key[0]) : key[0];
            if (ctrl)
            {
                c = char.ToUpperInvariant(c) switch
                {
                    >= 'A' and <= 'Z' and var l => (char)(l - 'A' + 1),
                    '@' or '2' or ' ' => '\0',
                    '[' => '\x1b',
                    '\\' => '\x1c',
                    ']' => '\x1d',
                    '^' or '6' => '\x1e',
                    '_' or '-' => '\x1f',
                    '?' => '\x7f',
                    _ => throw new ArgumentException($"ctrl+{key} has no terminal encoding"),
                };
            }
            seq = c.ToString();
        }
        else
        {
            throw new ArgumentException($"unknown key '{key}'");
        }
        return alt ? "\x1b" + seq : seq;
    }

    public static string ToSequence(IEnumerable<string> chords) => string.Concat(chords.Select(ToSequence));
}
