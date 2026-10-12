using System;
using System.Collections.Generic;
using System.IO;
using Bellboard.Services;

namespace Bellboard.Terminal;

/// <summary>
/// zsh (the usual WSL shell besides bash) ignores PROMPT_COMMAND, so Bellboard points ZDOTDIR at wrapper
/// startup files that load the user's own .zshenv/.zprofile/.zshrc/.zlogin unchanged and then add a
/// precmd hook reporting the cwd (OSC 7). The user's files are never modified. Same approach as VS Code.
/// </summary>
public static class ShellIntegration
{
    public static string ZshDir => Path.Combine(Log.Dir, "shell", "zsh");

    // Each wrapper swaps ZDOTDIR to the user's, sources their file, and swaps back so zsh keeps
    // reading the remaining startup files from here. .zlogin is last, so it leaves the user's ZDOTDIR.
    private static readonly Dictionary<string, string> Files = new()
    {
        [".zshenv"] = """
            # Bellboard shell integration (generated; safe to delete)
            BELLBOARD_ZDOTDIR="$ZDOTDIR"
            ZDOTDIR="${BELLBOARD_USER_ZDOTDIR:-$HOME}"
            [[ -f "$ZDOTDIR/.zshenv" ]] && source "$ZDOTDIR/.zshenv"
            BELLBOARD_USER_ZDOTDIR="$ZDOTDIR"
            ZDOTDIR="$BELLBOARD_ZDOTDIR"
            """,
        [".zprofile"] = """
            ZDOTDIR="$BELLBOARD_USER_ZDOTDIR"
            [[ -f "$ZDOTDIR/.zprofile" ]] && source "$ZDOTDIR/.zprofile"
            ZDOTDIR="$BELLBOARD_ZDOTDIR"
            """,
        [".zshrc"] = """
            ZDOTDIR="$BELLBOARD_USER_ZDOTDIR"
            [[ -f "$ZDOTDIR/.zshrc" ]] && source "$ZDOTDIR/.zshrc"
            __bellboard_osc7() { printf '\e]7;file://wsl.localhost/%s%s\a' "${WSL_DISTRO_NAME}" "${PWD}"; }
            autoload -Uz add-zsh-hook 2>/dev/null && add-zsh-hook precmd __bellboard_osc7 || precmd_functions+=(__bellboard_osc7)
            if [[ -o login ]]; then ZDOTDIR="$BELLBOARD_ZDOTDIR"; fi
            """,
        [".zlogin"] = """
            ZDOTDIR="$BELLBOARD_USER_ZDOTDIR"
            [[ -f "$ZDOTDIR/.zlogin" ]] && source "$ZDOTDIR/.zlogin"
            """,
    };

    /// <summary>Writes the wrapper files if missing or outdated; returns the directory, or null on failure.</summary>
    public static string? EnsureZsh()
    {
        try
        {
            Directory.CreateDirectory(ZshDir);
            foreach (var (name, body) in Files)
            {
                var path = Path.Combine(ZshDir, name);
                var content = body.Replace("\r\n", "\n") + "\n";
                if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            }
            return ZshDir;
        }
        catch (Exception ex)
        {
            Log.Error("writing zsh integration failed", ex);
            return null;
        }
    }
}
