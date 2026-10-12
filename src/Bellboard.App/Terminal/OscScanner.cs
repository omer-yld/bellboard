using System;
using System.Collections.Generic;
using System.Text;

namespace Bellboard.Terminal;

/// <summary>
/// Watches the output stream for the escape sequences Bellboard reacts to. The bytes still flow
/// to the renderer untouched; this only observes them.
///   OSC 0/2            window title
///   OSC 7              cwd as file:// URL (bash/zsh/fish convention)
///   OSC 9;9;"path"     cwd (Windows Terminal / ConEmu convention)
///   OSC 9;4;st;pct     progress (ConEmu / Windows Terminal)
///   OSC 9;text         notification (iTerm2)
///   OSC 99;meta;text   notification (kitty)
///   OSC 777;notify;t;b notification (rxvt / Ghostty)
///   BEL                bell
/// </summary>
public sealed class OscScanner
{
    public event Action<string>? Title;
    public event Action<string>? Cwd;
    public event Action<string?, string>? Notify;
    public event Action<int, int>? Progress;
    public event Action? Bell;

    private enum State { Normal, Esc, Osc, OscEsc }
    private const int MaxOsc = 8192;
    private State _state;
    private readonly StringBuilder _buf = new();
    private readonly Dictionary<string, (string? Title, string? Body)> _kitty = new();

    public void Feed(string text)
    {
        foreach (var c in text)
        {
            switch (_state)
            {
                case State.Normal:
                    if (c == '\x1b') _state = State.Esc;
                    else if (c == '\a') Bell?.Invoke();
                    break;
                case State.Esc:
                    if (c == ']') { _state = State.Osc; _buf.Clear(); }
                    else if (c != '\x1b') _state = State.Normal;
                    break;
                case State.Osc:
                    if (c == '\a' || c == '\u009c') { Dispatch(); _state = State.Normal; }
                    else if (c == '\x1b') _state = State.OscEsc;
                    else if (_buf.Length < MaxOsc) _buf.Append(c);
                    else _state = State.Normal; // runaway sequence, give up
                    break;
                case State.OscEsc:
                    if (c == '\\') { Dispatch(); _state = State.Normal; }
                    else if (c == ']') { _state = State.Osc; _buf.Clear(); }
                    else _state = State.Normal;
                    break;
            }
        }
    }

    private void Dispatch()
    {
        var s = _buf.ToString();
        _buf.Clear();
        var semi = s.IndexOf(';');
        var code = semi < 0 ? s : s[..semi];
        var rest = semi < 0 ? "" : s[(semi + 1)..];
        try
        {
            switch (code)
            {
                case "0":
                case "2":
                    Title?.Invoke(rest);
                    break;
                case "7":
                    if (ParseFileUrl(rest) is { } p) Cwd?.Invoke(p);
                    break;
                case "9":
                    HandleOsc9(rest);
                    break;
                case "99":
                    HandleKitty(rest);
                    break;
                case "777":
                    var parts = rest.Split(';', 3);
                    if (parts.Length >= 2 && parts[0] == "notify")
                        Notify?.Invoke(parts[1], parts.Length > 2 ? parts[2] : "");
                    break;
            }
        }
        catch
        {
            // Never let a malformed sequence break the output pipeline.
        }
    }

    private void HandleOsc9(string rest)
    {
        if (rest.StartsWith("9;"))
        {
            var path = rest[2..].Trim().Trim('"');
            if (path.Length > 0) Cwd?.Invoke(path);
            return;
        }
        if (rest.StartsWith("4;"))
        {
            var p = rest.Split(';');
            int st = p.Length > 1 && int.TryParse(p[1], out var a) ? a : 0;
            int pct = p.Length > 2 && int.TryParse(p[2], out var b) ? b : 0;
            Progress?.Invoke(st, pct);
            return;
        }
        // Other ConEmu OSC 9;<n> subcommands (1-12) are not notifications.
        var semi = rest.IndexOf(';');
        if (semi > 0 && int.TryParse(rest[..semi], out _)) return;
        if (int.TryParse(rest, out _)) return;
        if (rest.Length > 0) Notify?.Invoke(null, rest);
    }

    private void HandleKitty(string rest)
    {
        // OSC 99 ; key=value:key=value ; payload
        var semi = rest.IndexOf(';');
        var meta = semi < 0 ? rest : rest[..semi];
        var payload = semi < 0 ? "" : rest[(semi + 1)..];
        string id = "0", type = "title", done = "1", enc = "0";
        foreach (var kv in meta.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var k = kv[..eq];
            var v = kv[(eq + 1)..];
            switch (k)
            {
                case "i": id = v; break;
                case "p": type = v; break;
                case "d": done = v; break;
                case "e": enc = v; break;
            }
        }
        if (type is not ("title" or "body")) return;
        if (enc == "1")
        {
            try { payload = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); } catch { return; }
        }
        _kitty.TryGetValue(id, out var cur);
        if (type == "title") cur.Title = (cur.Title ?? "") + payload;
        else cur.Body = (cur.Body ?? "") + payload;
        if (done == "0")
        {
            _kitty[id] = cur;
            return;
        }
        _kitty.Remove(id);
        if (cur.Body is { Length: > 0 }) Notify?.Invoke(cur.Title, cur.Body);
        else if (cur.Title is { Length: > 0 }) Notify?.Invoke(null, cur.Title);
    }

    internal static string? ParseFileUrl(string url)
    {
        if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[7..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var host = rest[..slash];
        var path = Uri.UnescapeDataString(rest[slash..]);
        // file://wsl.localhost/<distro>/home/me (Bellboard's WSL prompt hook) → \\wsl.localhost\<distro>\home\me
        if (host.Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("wsl$", StringComparison.OrdinalIgnoreCase))
            return @"\\" + host + path.Replace('/', '\\');
        // file://host/C:/Users/me  ->  C:\Users\me
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            return path[1..].Replace('/', '\\');
        return path;
    }
}
