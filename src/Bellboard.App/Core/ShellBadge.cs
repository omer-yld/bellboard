using System.IO;
using System.Windows.Media;
using Bellboard.Panes;
using Bellboard.Terminal;

namespace Bellboard.Core;

/// <summary>
/// The coloured tile that says what runs in a pane (WSL, PS, PS5, CMD, GIT, SSH, WEB), with the short label
/// for its header ("pwsh", "Ubuntu-24.04") and the environment for the title bar and status bar.
/// </summary>
public sealed record ShellBadge(string Text, string Label, string Env, Brush Foreground, Brush Background)
{
    private static Brush B(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly (Brush Fg, Brush Bg) Wsl = (B(255, 0xFF, 0x8F, 0x5E), B(0x26, 0xE9, 0x60, 0x28));
    private static readonly (Brush Fg, Brush Bg) Ps = (B(255, 0x93, 0xB5, 0xFF), B(0x26, 0x60, 0x90, 0xFF));
    private static readonly (Brush Fg, Brush Bg) Cmd = (B(255, 0xC3, 0xC7, 0xD0), B(0x24, 0xA0, 0xA6, 0xB4));
    private static readonly (Brush Fg, Brush Bg) Git = (B(255, 0x6F, 0xDC, 0xA6), B(0x21, 0x5F, 0xD3, 0x9A));
    private static readonly (Brush Fg, Brush Bg) Ssh = (B(255, 0x7D, 0xD8, 0xD3), B(0x24, 0x62, 0xC7, 0xC2));
    private static readonly (Brush Fg, Brush Bg) Web = (B(255, 0xD2, 0xAE, 0xF5), B(0x24, 0xC9, 0x9B, 0xF0));

    /// <summary>The dot colour in the status bar, matching the badge.</summary>
    public Brush Dot => Foreground;

    /// <summary>The badge for a shell choice ("pwsh", "wsl:Ubuntu", "ssh:host"…) before any pane runs it.</summary>
    public static ShellBadge ForAlias(string alias)
    {
        var a = alias.ToLowerInvariant();
        if (a.StartsWith("wsl")) return new ShellBadge("WSL", alias.Length > 4 ? alias[4..] : "wsl", "WSL", Wsl.Fg, Wsl.Bg);
        if (a.StartsWith("ssh:")) return new ShellBadge("SSH", alias[4..], "SSH", Ssh.Fg, Ssh.Bg);
        return a switch
        {
            "pwsh" => new ShellBadge("PS", "pwsh", "PowerShell 7", Ps.Fg, Ps.Bg),
            "powershell" => new ShellBadge("PS5", "powershell", "Windows PowerShell", Ps.Fg, Ps.Bg),
            "cmd" => new ShellBadge("CMD", "cmd", "Command Prompt", Cmd.Fg, Cmd.Bg),
            "bash" or "gitbash" or "git-bash" => new ShellBadge("GIT", "bash", "Git Bash", Git.Fg, Git.Bg),
            _ => new ShellBadge(a.Length > 4 ? a[..4].ToUpperInvariant() : a.ToUpperInvariant(), alias, alias, Cmd.Fg, Cmd.Bg),
        };
    }

    public static ShellBadge For(PaneBase? pane)
    {
        switch (pane)
        {
            case BrowserPane b:
                return new ShellBadge("WEB", b.DisplayTitle ?? "Browser", "Browser", Web.Fg, Web.Bg);
            case TerminalPane { Feed: true } f:
                return new ShellBadge(f.FeedBadge ?? "FEED", f.FeedLabel ?? "feed", f.FeedLabel ?? "Feed", Web.Fg, Web.Bg);
            case TerminalPane t when t.Remote is { } host:
                return new ShellBadge("SSH", host, "SSH · " + host, Ssh.Fg, Ssh.Bg);
            case TerminalPane t:
                switch (t.ShellKind)
                {
                    case ShellKind.Pwsh: return new ShellBadge("PS", "pwsh", "PowerShell 7", Ps.Fg, Ps.Bg);
                    case ShellKind.WindowsPowerShell: return new ShellBadge("PS5", "powershell", "Windows PowerShell", Ps.Fg, Ps.Bg);
                    case ShellKind.Cmd: return new ShellBadge("CMD", "cmd", "Command Prompt", Cmd.Fg, Cmd.Bg);
                    case ShellKind.Bash: return new ShellBadge("GIT", "bash", "Git Bash", Git.Fg, Git.Bg);
                    case ShellKind.Wsl:
                        var distro = t.Shell is { } s && s.StartsWith("wsl:", System.StringComparison.OrdinalIgnoreCase) ? s[4..]
                            : ShellResolver.ParseWslPath(t.Cwd)?.Distro;
                        return new ShellBadge("WSL", distro ?? "wsl", distro != null ? "WSL · " + distro : "WSL", Wsl.Fg, Wsl.Bg);
                    default:
                        var exe = t.Shell is { Length: > 0 } other ? Path.GetFileNameWithoutExtension(other.Trim('"').Split(' ')[0]) : "shell";
                        var text = exe.Length > 4 ? exe[..4].ToUpperInvariant() : exe.ToUpperInvariant();
                        return new ShellBadge(text, exe, exe, Cmd.Fg, Cmd.Bg);
                }
            default:
                return new ShellBadge("—", "", "", Cmd.Fg, Cmd.Bg);
        }
    }
}
