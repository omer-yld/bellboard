using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bellboard.Terminal;

public enum ShellKind { Pwsh, WindowsPowerShell, Cmd, Bash, Wsl, Other }

/// <param name="Env">Extra environment variables for this shell (integration hooks).</param>
/// <param name="TypedCommand">For shells that can't take a startup command as an argument: typed after the first prompt.</param>
public sealed record ShellSpec(string CommandLine, ShellKind Kind, string Exe)
{
    public Dictionary<string, string> Env { get; init; } = new();
    public string? TypedCommand { get; init; }
}

/// <summary>Builds the command line for a pane: the configured (or detected) shell plus Bellboard shell integration.</summary>
public static class ShellResolver
{
    // Emits OSC 9;9 with the current directory before every prompt so Bellboard can show cwd + git branch.
    // Passed via -EncodedCommand, so it works under any execution policy and leaves $PROFILE untouched.
    private const string PowerShellIntegration = """
        if (-not $global:__bellboardPromptWrapped) {
          $global:__bellboardPromptWrapped = $true
          $global:__bellboardPrompt = $function:prompt
          function global:prompt {
            $e = [char]27
            $loc = $executionContext.SessionState.Path.CurrentLocation
            $osc = ''
            if ($loc.Provider.Name -eq 'FileSystem') { $osc = "$e]9;9;`"$($loc.ProviderPath)`"$e\" }
            $p = if ($global:__bellboardPrompt) { & $global:__bellboardPrompt } else { "PS $($loc.Path)> " }
            "$osc$p"
          }
        }
        """;

    // bash (Git Bash, WSL) reads PROMPT_COMMAND from the environment and reports its cwd with OSC 7.
    private const string GitBashPromptCommand = @"printf '\e]7;file://%s/%s\a' ""$HOSTNAME"" ""$(pwd -W 2>/dev/null || pwd)""";
    private const string WslPromptCommand = @"printf '\e]7;file://wsl.localhost/%s%s\a' ""$WSL_DISTRO_NAME"" ""$PWD""";
    /// <summary>Variables forwarded into WSL so the Bellboard CLI and hooks work from Linux shells too.</summary>
    private const string WslForward = "PROMPT_COMMAND:" + WslPaneVariables + ":TERM_PROGRAM:TERM_PROGRAM_VERSION:COLORTERM";

    /// <summary>
    /// What WSL needs to see of a pane's environment so `bell.exe` and agent hooks run from Linux still reach this
    /// pane. Every pane sets it, so a `wsl` typed at a PowerShell prompt gets it too.
    /// </summary>
    public const string WslPaneVariables =
        "BELLBOARD_PANE_ID:BELLBOARD_WORKSPACE_ID:BELLBOARD_PIPE:BELLBOARD:BELLBOARD_AGENT_TASK:BELLBOARD_AGENT_BRANCH";

    /// <summary>Short names accepted wherever a shell can be chosen (config, --shell, settings).</summary>
    public static string? ExpandAlias(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell)) return null;
        var s = shell.Trim();
        switch (s.ToLowerInvariant())
        {
            case "pwsh":
                return (FindOnPath("pwsh.exe") ?? KnownPwsh()) is { } pwsh ? Quote(pwsh) : null;
            case "powershell":
                return Quote(WindowsPowerShellPath);
            case "cmd":
                return Quote(Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            case "wsl":
                return "wsl.exe";
            case "bash" or "gitbash" or "git-bash":
                return GitBashPath() is { } bash ? Quote(bash) : null;
        }
        if (s.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase)) return $"wsl.exe -d {s[4..]}";
        // ssh:<host> (a Host from ~/.ssh/config, or user@host and any ssh options)
        if (s.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
            return s[4..].Trim() is { Length: > 0 } target && SshPath() is { } ssh ? $"{Quote(ssh)} {target}" : null;
        return s;
    }

    /// <summary>Windows' OpenSSH client (System32\OpenSSH\ssh.exe), or another ssh.exe on PATH.</summary>
    public static string? SshPath()
    {
        var builtIn = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        return File.Exists(builtIn) ? builtIn : FindOnPath("ssh.exe");
    }

    /// <summary>Host aliases from ~/.ssh/config, for the shell menu.</summary>
    public static List<string> SshHosts()
    {
        try
        {
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            return File.Exists(config) ? ParseSshHosts(File.ReadAllText(config)) : new();
        }
        catch { return new(); }
    }

    /// <summary>The concrete names on `Host` lines (patterns with *, ? or ! are skipped).</summary>
    public static List<string> ParseSshHosts(string config)
    {
        var hosts = new List<string>();
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            // "Host a b", "Host=a b" or "Host = a b"; names may be quoted.
            var m = Regex.Match(line, @"^(\w+)\s*(?:=\s*|\s+)(.+)$");
            if (!m.Success || !m.Groups[1].Value.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var v in SplitArgs(m.Groups[2].Value))
                if (v.Length > 0 && v.IndexOfAny(['*', '?', '!']) < 0 && !hosts.Contains(v, StringComparer.OrdinalIgnoreCase)) hosts.Add(v);
        }
        return hosts;
    }

    /// <summary>
    /// When the user started wsl or ssh at a pane's prompt, the shell a new tab or split from that pane should run
    /// ("wsl", "wsl:Ubuntu", "ssh:user@host -p 2222"); null for anything else.
    /// </summary>
    public static string? NestedShellAlias(string exe, string? commandLine)
    {
        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        var args = SplitArgs(commandLine ?? "").Skip(1).ToList();
        switch (name)
        {
            case "wsl":
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] is "-d" or "--distribution" && i + 1 < args.Count) return "wsl:" + args[i + 1];
                    if (args[i] is "-e" or "--exec" or "--") return null; // running a command, not a shell
                }
                return "wsl";
            case "ssh":
                // ssh with options and a host is an interactive session; keep its options (port, key, user).
                return args.Count > 0 ? "ssh:" + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) : null;
            default:
                return null;
        }
    }

    /// <summary>The host an "ssh:..." shell connects to ("ssh:-p 2222 me@devbox" → "devbox"); null for other shells.</summary>
    public static string? SshDestination(string? alias)
    {
        if (alias == null || !alias.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase)) return null;
        var args = SplitArgs(alias[4..]);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                // ssh options that take a value (see ssh(1)); skip the value too.
                if (args[i].Length == 2 && "BbcDEeFIiJLlmOoPpQRSWw".Contains(args[i][1])) i++;
                continue;
            }
            var dest = args[i];
            if (dest.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) dest = dest[6..];
            dest = dest[(dest.LastIndexOf('@') + 1)..];
            var colon = dest.LastIndexOf(':');
            return colon > 0 && !dest.Contains('[') && dest.Count(c => c == ':') == 1 ? dest[..colon] : dest;
        }
        return null;
    }

    /// <summary>Splits a Windows command line on spaces, honouring double quotes.</summary>
    internal static List<string> SplitArgs(string commandLine)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (c is ' ' or '\t' && !quoted)
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }

    private static string WindowsPowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string? GitBashPath()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string> { Path.Combine(pf, "Git", "bin", "bash.exe") };
        if (FindOnPath("git.exe") is { } git) candidates.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Installed shells for pickers: (alias, label). SSH hosts only where a remote default makes sense.</summary>
    public static List<(string Alias, string Label)> Available(bool includeSsh = false)
    {
        var list = new List<(string, string)>();
        if ((FindOnPath("pwsh.exe") ?? KnownPwsh()) != null) list.Add(("pwsh", "PowerShell 7"));
        list.Add(("powershell", "Windows PowerShell"));
        list.Add(("cmd", "Command Prompt"));
        if (GitBashPath() != null) list.Add(("bash", "Git Bash"));
        foreach (var d in WslDistros()) list.Add(("wsl:" + d, "WSL · " + d));
        if (includeSsh && SshPath() != null)
            foreach (var h in SshHosts()) list.Add(("ssh:" + h, "SSH · " + h));
        return list;
    }

    public static List<string> WslDistros()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (key == null) return new();
            return key.GetSubKeyNames()
                .Select(k => key.OpenSubKey(k)?.GetValue("DistributionName") as string)
                .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>("Distro", "/home/me") → "\\wsl.localhost\Distro\home\me", the same folder as Windows sees it.</summary>
    public static string WslUncPath(string distro, string linuxPath) => $@"\\wsl.localhost\{distro}" + linuxPath.Replace('/', '\\');

    /// <summary>"\\wsl.localhost\Distro\home\me" → ("Distro", "/home/me").</summary>
    public static (string Distro, string LinuxPath)? ParseWslPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('\\');
            var distro = slash < 0 ? rest : rest[..slash];
            var linux = slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
            return distro.Length > 0 ? (distro, linux) : null;
        }
        return null;
    }

    /// <param name="zshIntegrationDir">Windows folder with Bellboard's zsh wrapper files (see ShellIntegration), for WSL.</param>
    public static ShellSpec Resolve(string? configured, string? command, string? cwd = null, string? zshIntegrationDir = null)
    {
        string baseCmd;
        if (ExpandAlias(configured) is { } explicitShell)
            baseCmd = explicitShell;
        else if ((FindOnPath("pwsh.exe") ?? KnownPwsh()) is { } pwsh)
            baseCmd = Quote(pwsh);
        else
            baseCmd = Quote(WindowsPowerShellPath);

        var exe = FirstToken(baseCmd);
        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        var kind = name switch
        {
            "pwsh" => ShellKind.Pwsh,
            "powershell" => ShellKind.WindowsPowerShell,
            "cmd" => ShellKind.Cmd,
            "wsl" => ShellKind.Wsl,
            // System32\bash.exe is the legacy WSL launcher; anything else named bash is Git Bash/MSYS.
            "bash" when exe.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase) => ShellKind.Wsl,
            "bash" => ShellKind.Bash,
            _ => ShellKind.Other,
        };
        var typed = string.IsNullOrWhiteSpace(command) ? null : command;

        switch (kind)
        {
            case ShellKind.Pwsh:
            case ShellKind.WindowsPowerShell:
                if (HasAnyArg(baseCmd, "-command", "-c", "-file", "-f", "-encodedcommand", "-ec", "-e"))
                    return new ShellSpec(baseCmd, kind, exe);
                var script = PowerShellIntegration + (string.IsNullOrWhiteSpace(command) ? "" : "\n" + command);
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var noLogo = HasAnyArg(baseCmd, "-nologo") ? "" : " -NoLogo";
                return new ShellSpec($"{baseCmd}{noLogo} -NoExit -EncodedCommand {encoded}", kind, exe);
            case ShellKind.Cmd:
                // cmd's prompt integration comes from the PROMPT environment variable.
                return new ShellSpec(string.IsNullOrWhiteSpace(command) ? baseCmd : $"{baseCmd} /k {command}", kind, exe)
                {
                    Env = { ["PROMPT"] = CmdPrompt },
                };
            case ShellKind.Bash:
                // -l for the normal Git Bash profile; CHERE_INVOKING keeps the start folder; MSYSTEM gives the usual prompt.
                return new ShellSpec(baseCmd.Trim() == Quote(exe) || baseCmd.Trim() == exe ? $"{baseCmd} -i -l" : baseCmd, kind, exe)
                {
                    Env = { ["PROMPT_COMMAND"] = GitBashPromptCommand, ["CHERE_INVOKING"] = "1", ["MSYSTEM"] = "MINGW64" },
                    TypedCommand = typed,
                };
            case ShellKind.Wsl:
            {
                var cmdLine = baseCmd;
                if (ParseWslPath(cwd) is { } wsl)
                {
                    if (!HasAnyArg(baseCmd, "-d", "--distribution")) cmdLine += $" -d {wsl.Distro}";
                    cmdLine += $" --cd \"{wsl.LinuxPath}\"";
                }
                var forward = WslForward + (zshIntegrationDir != null ? ":ZDOTDIR/p" : "");
                var existing = Environment.GetEnvironmentVariable("WSLENV");
                var spec = new ShellSpec(cmdLine, kind, exe)
                {
                    Env =
                    {
                        ["PROMPT_COMMAND"] = WslPromptCommand,
                        ["WSLENV"] = string.IsNullOrEmpty(existing) ? forward : existing.TrimEnd(':') + ":" + forward,
                    },
                    TypedCommand = typed,
                };
                // zsh ignores PROMPT_COMMAND; ZDOTDIR points it at wrappers that add a precmd hook.
                if (zshIntegrationDir != null) spec.Env["ZDOTDIR"] = zshIntegrationDir;
                return spec;
            }
            default:
                return new ShellSpec(baseCmd, kind, exe) { TypedCommand = typed };
        }
    }

    public const string CmdPrompt = "$e]9;9;$P$e\\$P$G";

    private static bool HasAnyArg(string cmd, params string[] names)
    {
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.ToLowerInvariant());
        return tokens.Any(t => names.Contains(t));
    }

    private static string FirstToken(string cmd)
    {
        cmd = cmd.TrimStart();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? cmd[1..end] : cmd.Trim('"');
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? cmd : cmd[..sp];
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static string? KnownPwsh()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), exe);
                // Skip the WindowsApps execution aliases; they fail inside a pseudoconsole on some builds.
                if (File.Exists(full) && !full.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return full;
            }
            catch { }
        }
        return null;
    }

    public static Dictionary<string, string> BaseEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        return env;
    }
}
