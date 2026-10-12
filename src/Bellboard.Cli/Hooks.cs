using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bellboard.Shared;

namespace Bellboard.Cli;

/// <summary>
/// Agent integrations. `bell hooks install` registers Bellboard in Claude Code's hooks (and Codex's
/// `notify`), so agents light the pane ring, update the sidebar status, and raise a toast.
/// The hook command is a no-op outside Bellboard (no BELLBOARD_PANE_ID) and never fails the agent.
/// </summary>
internal static class Hooks
{
    private const string CodexMarker = "\"hook\", \"codex\"";
    private static readonly string[] ClaudeEvents =
        ["SessionStart", "UserPromptSubmit", "Notification", "Stop", "SessionEnd", "SubagentStart", "SubagentStop", "TeammateIdle", "TaskCompleted"];

    public static int RunHook(string[] args)
    {
        try
        {
            var agent = args.Length > 0 ? args[0] : "claude";
            string ev;
            JsonNode? payload;
            if (agent == "codex")
            {
                // Codex runs: <notify command...> '<json>'
                ev = "notify";
                var (chain, raw) = ParseCodexArgs(args);
                if (chain is { Length: > 0 }) PassOn(chain, raw);
                payload = TryParse(raw);
            }
            else
            {
                ev = args.Length > 1 ? args[1] : "Notification";
                payload = TryParse(ReadStdin(2000));
            }
            var pane = BellboardProtocol.Env(BellboardProtocol.EnvPane);
            if (string.IsNullOrEmpty(pane)) return 0;
            using var client = PipeClient.TryConnect(BellboardProtocol.ResolvePipeName(), 1000);
            client?.Call("agent.hook", new JsonObject { ["pane"] = pane, ["agent"] = agent, ["event"] = ev, ["payload"] = payload }, 5000);
        }
        catch
        {
            // Hooks must never break the agent.
        }
        return 0;
    }

    private static string? ReadStdin(int timeoutMs)
    {
        if (!Console.IsInputRedirected) return null;
        var t = Task.Run(() => Console.In.ReadToEnd());
        return t.Wait(timeoutMs) ? t.Result : null;
    }

    private static JsonNode? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonNode.Parse(s); } catch { return null; }
    }

    public static int Manage(string action, bool wsl = false)
    {
        var claude = ClaudeSettingsPath();
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch
            ? ch
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var codex = Path.Combine(codexHome, "config.toml");
        var piDir = PiAgentDir();
        switch (action)
        {
            case "install":
            {
                var exe = ExePath();
                var added = InstallClaude(claude, exe, out var replaced);
                Console.WriteLine(added == 0 ? $"Claude Code: already installed in {claude}"
                    : replaced > 0 ? $"Claude Code: replaced {replaced} older wmux/Bellboard hook(s) and added {added} in {claude}"
                    : $"Claude Code: added {added} hook(s) to {claude}");
                Console.WriteLine(InstallCodex(codex, exe) switch
                {
                    CodexResult.Added => $"Codex: added notify to {codex}",
                    CodexResult.Updated => $"Codex: updated the notify command in {codex}",
                    CodexResult.AlreadyInstalled => "Codex: already installed",
                    CodexResult.Chained => $"Codex: {codex} already had a notify command; Bellboard now runs first and passes each notification on to it",
                    CodexResult.OtherNotify => $"Codex: couldn't read the `notify` setting in {codex}; add [\"{exe}\", \"hook\", \"codex\"] yourself if you want Bellboard notifications.",
                    _ => "Codex: not installed (no ~/.codex), skipped",
                });
                if (Directory.Exists(piDir))
                    Console.WriteLine($"Pi: {Describe(InstallPi(piDir, exe), PiExtensionPath(piDir))}");
                InstallWsl(wsl);
                Console.WriteLine("Restart running agents to pick up the hooks.");
                return 0;
            }
            case "uninstall":
                if (wsl)
                    foreach (var t in WslTargets())
                    {
                        if (t.HasClaude) Console.WriteLine($"WSL · {t.Distro}: Claude Code: removed {UninstallClaude(t.ClaudeSettings)} hook(s)");
                        if (t.HasCodex && UninstallCodex(t.CodexConfig)) Console.WriteLine($"WSL · {t.Distro}: Codex: removed notify");
                        if (t.HasPi && UninstallPi(t.PiAgentDir)) Console.WriteLine($"WSL · {t.Distro}: Pi: removed the Bellboard extension");
                    }
                if (UninstallPi(piDir)) Console.WriteLine("Pi: removed the Bellboard extension");
                Console.WriteLine($"Claude Code: removed {UninstallClaude(claude)} hook(s)");
                if (UninstallCodex(codex, out var restored))
                    Console.WriteLine(restored ? "Codex: removed Bellboard from notify and put the previous command back" : "Codex: removed notify");
                return 0;
            case "status":
                Console.WriteLine($"Claude Code ({claude}): {(IsClaudeInstalled(claude) ? "installed" : "not installed")}");
                Console.WriteLine($"Codex       ({codex}): {(File.Exists(codex) && File.ReadAllText(codex).Contains(CodexMarker) ? "installed" : "not installed")}");
                if (Directory.Exists(piDir))
                    Console.WriteLine($"Pi          ({PiExtensionPath(piDir)}): {(IsPiInstalled(piDir) ? "installed" : "not installed")}");
                foreach (var t in WslTargets())
                {
                    if (t.HasClaude)
                        Console.WriteLine($"Claude Code in WSL · {t.Distro}: {(IsClaudeInstalled(t.ClaudeSettings) ? "installed" : "not installed (bell hooks install --wsl)")}");
                    if (t.HasPi)
                        Console.WriteLine($"Pi in WSL · {t.Distro}: {(IsPiInstalled(t.PiAgentDir) ? "installed" : "not installed (bell hooks install --wsl)")}");
                }
                Console.WriteLine($"inside Bellboard: {(BellboardProtocol.Env(BellboardProtocol.EnvPane) is { Length: > 0 } p ? "yes, pane " + p : "no")}");
                return 0;
            default:
                throw new BellboardException("usage: bell hooks install|uninstall|status [--wsl]");
        }
    }

    /// <summary>A WSL distro where Claude Code or Codex is set up. Paths are as Windows sees them (\\wsl.localhost\...).</summary>
    internal sealed record WslTarget(string Distro, string Home, string ClaudeSettings, string CodexConfig, string PiAgentDir,
        bool HasClaude, bool HasCodex, bool HasPi);

    /// <summary>
    /// Agents running inside WSL report to Bellboard by calling the Windows bell.exe through its /mnt/... path
    /// (the pane's variables reach them through WSLENV). Only with --wsl, since it edits the distros' own config.
    /// </summary>
    private static void InstallWsl(bool wsl)
    {
        var targets = WslTargets().Where(t => t.HasClaude || t.HasCodex || t.HasPi).ToList();
        if (!wsl)
        {
            foreach (var t in targets)
            {
                var agents = new[] { (t.HasClaude, "Claude Code"), (t.HasCodex, "Codex"), (t.HasPi, "Pi") }.Where(a => a.Item1).Select(a => a.Item2);
                Console.WriteLine($"WSL · {t.Distro} has {string.Join(", ", agents)} set up too: run `bell hooks install --wsl` so agents running there report to Bellboard.");
            }
            return;
        }
        if (targets.Count == 0) Console.WriteLine("WSL: no distro has Claude Code (~/.claude), Codex (~/.codex) or Pi (~/.pi) set up");
        foreach (var t in targets)
        {
            var exe = WslExePath(t.Distro);
            if (exe == null)
            {
                Console.WriteLine($"WSL · {t.Distro}: couldn't map bell.exe to a Linux path (is /mnt/c mounted?), skipped");
                continue;
            }
            if (t.HasClaude)
            {
                // Claude runs the hook through a shell, so a path with spaces needs quotes; Codex gets a list of arguments.
                var command = exe.Contains(' ') ? "'" + exe.Replace("'", "'\\''") + "'" : exe;
                var added = InstallClaude(t.ClaudeSettings, command, out var replaced);
                Console.WriteLine(added == 0 ? $"WSL · {t.Distro}: Claude Code: already installed"
                    : $"WSL · {t.Distro}: Claude Code: {(replaced > 0 ? $"replaced {replaced} older hook(s) and " : "")}added {added} hook(s) to {t.Home}/.claude/settings.json");
            }
            if (t.HasCodex)
                Console.WriteLine(InstallCodex(t.CodexConfig, exe, chain: false) switch
                {
                    CodexResult.Added => $"WSL · {t.Distro}: Codex: added notify to {t.Home}/.codex/config.toml",
                    CodexResult.Updated => $"WSL · {t.Distro}: Codex: updated the notify command",
                    CodexResult.AlreadyInstalled => $"WSL · {t.Distro}: Codex: already installed",
                    _ => $"WSL · {t.Distro}: Codex already has its own notify command; left it alone",
                });
            if (t.HasPi)
                Console.WriteLine($"WSL · {t.Distro}: Pi: {Describe(InstallPi(t.PiAgentDir, exe), t.Home + "/.pi/agent/extensions/" + PiExtensionFile)}");
        }
    }

    // ---- pi (https://github.com/earendil-works/pi): an extension instead of hooks ----

    internal enum PiResult { Added, Updated, AlreadyInstalled, Other }
    private const string PiExtensionFile = "bellboard.ts";
    private const string PiMarker = "// Bellboard for pi:";

    /// <summary>pi's config folder: PI_CODING_AGENT_DIR, else ~/.pi/agent.</summary>
    private static string PiAgentDir() =>
        Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");

    private static string PiExtensionPath(string agentDir) => Path.Combine(agentDir, "extensions", PiExtensionFile);

    /// <summary>The extension source with this bell.exe filled in (pi starts it directly, so no shell quoting).</summary>
    internal static string PiExtensionSource(string bell)
    {
        using var res = typeof(Hooks).Assembly.GetManifestResourceStream("bellboard.pi-extension.ts") ?? throw new BellboardException("pi extension resource missing");
        using var reader = new StreamReader(res);
        return reader.ReadToEnd().Replace("\"__BELL__\"", JsonSerializer.Serialize(bell));
    }

    /// <summary>Writes ~/.pi/agent/extensions/bellboard.ts; leaves a file of that name alone if Bellboard didn't write it.</summary>
    internal static PiResult InstallPi(string agentDir, string bell)
    {
        var path = PiExtensionPath(agentDir);
        var source = PiExtensionSource(bell);
        var existed = File.Exists(path);
        if (existed)
        {
            var current = File.ReadAllText(path);
            if (!current.StartsWith(PiMarker)) return PiResult.Other;
            if (current == source) return PiResult.AlreadyInstalled;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return existed ? PiResult.Updated : PiResult.Added;
    }

    internal static bool IsPiInstalled(string agentDir) =>
        File.Exists(PiExtensionPath(agentDir)) && File.ReadAllText(PiExtensionPath(agentDir)).StartsWith(PiMarker);

    internal static bool UninstallPi(string agentDir)
    {
        if (!IsPiInstalled(agentDir)) return false;
        File.Delete(PiExtensionPath(agentDir));
        return true;
    }

    private static string Describe(PiResult result, string path) => result switch
    {
        PiResult.Added => $"added the Bellboard extension ({path})",
        PiResult.Updated => $"updated the Bellboard extension ({path})",
        PiResult.AlreadyInstalled => "already installed",
        _ => $"{path} exists and wasn't written by Bellboard; left it alone",
    };

    internal static List<WslTarget> WslTargets()
    {
        var list = new List<WslTarget>();
        foreach (var distro in WslDistros())
        {
            var (code, home) = RunWsl(distro, "sh", "-c", "printf %s \"$HOME\"");
            if (code != 0 || !home.StartsWith('/')) continue;
            var unc = $@"\\wsl.localhost\{distro}" + home.Replace('/', '\\');
            var pi = Path.Combine(unc, ".pi", "agent");
            list.Add(new WslTarget(distro, home, Path.Combine(unc, ".claude", "settings.json"), Path.Combine(unc, ".codex", "config.toml"), pi,
                Directory.Exists(Path.Combine(unc, ".claude")), Directory.Exists(Path.Combine(unc, ".codex")), Directory.Exists(pi)));
        }
        return list;
    }

    private static List<string> WslDistros()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return key == null ? new() : key.GetSubKeyNames()
                .Select(k => key.OpenSubKey(k)?.GetValue("DistributionName") as string)
                .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!).ToList();
        }
        catch { return new(); }
    }

    /// <summary>This bell.exe as the distro sees it (/mnt/c/...).</summary>
    private static string? WslExePath(string distro)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "bell.exe");
        var (code, linux) = RunWsl(distro, "wslpath", "-u", exe);
        return code == 0 && linux.StartsWith('/') ? linux : null;
    }

    private static (int Code, string Out) RunWsl(string distro, params string[] command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-d", distro, "--exec" }.Concat(command)) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, output.Result.Trim());
        }
        catch { return (-1, ""); }
    }

    public const string McpName = "bellboard";

    /// <summary>
    /// `bell mcp install`: registers `bell mcp` with Claude Code via its own CLI, replacing the registration
    /// of the pre-rename `wmux mcp` and one that points at an older install of bell.exe.
    /// </summary>
    public static int InstallMcp()
    {
        var exe = ExePath();
        var claude = Terminal.ShellResolverLite.FindOnPath("claude.exe") ?? Terminal.ShellResolverLite.FindOnPath("claude.cmd");
        var snippet = $"{{\"mcpServers\": {{\"{McpName}\": {{\"command\": \"{exe}\", \"args\": [\"mcp\"]}}}}}}";
        if (claude == null)
        {
            Console.WriteLine("Claude Code CLI not found on PATH. Add this to your MCP client config:");
            Console.WriteLine(snippet);
            return 0;
        }
        var servers = LoadJson(ClaudeJsonPath())["mcpServers"] as JsonObject;
        if (servers?["wmux"]?["command"] is JsonValue legacy && ((string?)legacy ?? "").Contains("wmux", StringComparison.OrdinalIgnoreCase))
        {
            var (code, output) = RunClaude(claude, "mcp", "remove", "wmux", "--scope", "user");
            Console.WriteLine(code == 0 ? "removed the old wmux MCP server" : output);
        }
        if (servers?[McpName]?["command"] is JsonValue current)
        {
            if (string.Equals((string?)current, exe, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{McpName} is already registered with Claude Code");
                return 0;
            }
            RunClaude(claude, "mcp", "remove", McpName, "--scope", "user");
        }
        var (exit, result) = RunClaude(claude, "mcp", "add", "--scope", "user", McpName, "--", exe, "mcp");
        Console.WriteLine(result.Length > 0 ? result : $"registered {McpName} with Claude Code");
        return exit;
    }

    private static (int Code, string Output) RunClaude(string claude, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(claude) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>`bell skill install`: writes the skill to ~/.claude/skills/bellboard/SKILL.md (and removes the old wmux one).</summary>
    public static int InstallSkill(string action)
    {
        var skills = Path.Combine(Path.GetDirectoryName(ClaudeSettingsPath())!, "skills");
        var path = Path.Combine(skills, "bellboard", "SKILL.md");
        var removedLegacy = RemoveLegacySkill(skills);
        if (removedLegacy != null) Console.WriteLine($"removed the old wmux skill ({removedLegacy})");
        if (action == "uninstall")
        {
            if (File.Exists(path)) File.Delete(path);
            Console.WriteLine($"removed {path}");
            return 0;
        }
        using var res = typeof(Hooks).Assembly.GetManifestResourceStream("bellboard.SKILL.md") ?? throw new BellboardException("skill resource missing");
        using var reader = new StreamReader(res);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, reader.ReadToEnd());
        Console.WriteLine($"installed {path}");
        return 0;
    }

    /// <summary>Deletes skills/wmux/SKILL.md only if wmux wrote it (its front matter says "name: wmux").</summary>
    internal static string? RemoveLegacySkill(string skillsDir)
    {
        var path = Path.Combine(skillsDir, "wmux", "SKILL.md");
        if (!File.Exists(path) || !File.ReadLines(path).Take(3).Any(l => l.Trim() == "name: wmux")) return null;
        File.Delete(path);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return path;
    }

    /// <summary>~/.claude.json (user-scope MCP servers), or the one inside CLAUDE_CONFIG_DIR.</summary>
    internal static string ClaudeJsonPath() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? Path.Combine(d, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    private static string ClaudeSettingsPath()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, "settings.json");
    }

    /// <summary>Path to bell.exe that works unquoted in bash, cmd and PowerShell.</summary>
    private static string ExePath()
    {
        var path = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "bell.exe");
        if (path.Contains(' '))
        {
            var sb = new StringBuilder(1024);
            if (GetShortPathName(path, sb, sb.Capacity) > 0) path = sb.ToString();
        }
        return path.Replace('\\', '/');
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    private static readonly JsonDocumentOptions ReadOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject LoadJson(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOpts) as JsonObject ?? new JsonObject() : new JsonObject();

    private static void SaveJson(string path, JsonObject o)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".bellboard-backup", true);
        File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>A hook this CLI added: `…/bell.exe hook claude <Event>`, or `…/wmux.exe hook claude …` from before the rename.</summary>
    private static readonly Regex OurHook = new(@"(^|[\\/""'\s])(bell|wmux)(\.exe)?[""']?\s+hook\s+claude\b", RegexOptions.IgnoreCase);

    private static bool IsBellboardHook(JsonNode? h) => OurHook.IsMatch((string?)h?["command"] ?? "");

    private static string HookCommand(string exe, string ev) => $"{exe} hook claude {ev}";

    internal enum CodexResult { NoCodex, AlreadyInstalled, OtherNotify, Added, Updated, Chained }

    internal static bool IsClaudeInstalled(string path)
    {
        if (!File.Exists(path)) return false;
        var hooks = LoadJson(path)["hooks"] as JsonObject;
        return hooks != null && ClaudeEvents.All(ev =>
            (hooks[ev] as JsonArray)?.Any(m => (m?["hooks"] as JsonArray)?.Any(IsBellboardHook) == true) == true);
    }

    internal static int InstallClaude(string path, string exe) => InstallClaude(path, exe, out _);

    /// <summary>
    /// Adds the Bellboard hook to each Claude Code event that lacks one. Hooks that point elsewhere (wmux.exe
    /// from before the rename, or bell.exe from another folder) are replaced. Returns how many were added.
    /// </summary>
    internal static int InstallClaude(string path, string exe, out int replaced)
    {
        var root = LoadJson(path);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        int added = 0;
        replaced = 0;
        foreach (var ev in ClaudeEvents)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            var command = HookCommand(exe, ev);
            if (list.Any(m => (m?["hooks"] as JsonArray)?.Any(h => (string?)h?["command"] == command) == true)) continue;
            foreach (var m in list.ToList())
            {
                if (m?["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.Where(IsBellboardHook).ToList()) { hs.Remove(h); replaced++; }
                if (hs.Count == 0) list.Remove(m);
            }
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 10,
                }),
            });
            added++;
        }
        if (added > 0) SaveJson(path, root);
        return added;
    }

    /// <summary>Removes only Bellboard's hook entries, leaving the user's own hooks. Returns how many were removed.</summary>
    internal static int UninstallClaude(string path)
    {
        if (!File.Exists(path)) return 0;
        var root = LoadJson(path);
        if (root["hooks"] is not JsonObject hooks) return 0;
        int removed = 0;
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray list) continue;
            foreach (var m in list.ToList())
            {
                if (m?["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.Where(IsBellboardHook).ToList()) { hs.Remove(h); removed++; }
                if (hs.Count == 0) list.Remove(m);
            }
            if (list.Count == 0) hooks.Remove(ev);
        }
        if (removed > 0) SaveJson(path, root);
        return removed;
    }

    /// <summary>
    /// Codex runs its one `notify` command as `&lt;command...&gt; '&lt;json&gt;'`. If another tool already owns it
    /// (the Codex app's computer-use helper does), install wraps that command:
    /// `[bell, "hook", "codex", "--then", &lt;their command...&gt;]`, and the hook passes each notification on.
    /// </summary>
    internal static CodexResult InstallCodex(string path, string exe, bool chain = true)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) return CodexResult.NoCodex;
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var lines = text.Split('\n');
        var i = TopLevelNotify(lines);
        if (i < 0)
        {
            if (File.Exists(path)) File.Copy(path, path + ".bellboard-backup", true);
            File.WriteAllText(path, FormatNotify([exe, "hook", "codex"]) + "\n" + text);
            return CodexResult.Added;
        }
        if (ParseTomlStrings(lines[i].TrimEnd('\r')) is not { Count: > 0 } items) return CodexResult.OtherNotify;
        var ours = IsOurNotify(items);
        if (!ours && !chain) return CodexResult.OtherNotify;
        // Ours: point it at this bell.exe and keep any chain. Theirs: run it after us.
        List<string> wanted = ours ? [exe, "hook", "codex", .. items.Skip(3)] : [exe, "hook", "codex", ChainFlag, .. items];
        if (items.SequenceEqual(wanted)) return CodexResult.AlreadyInstalled;
        File.Copy(path, path + ".bellboard-backup", true);
        lines[i] = FormatNotify(wanted) + (lines[i].EndsWith('\r') ? "\r" : "");
        File.WriteAllText(path, string.Join('\n', lines));
        return ours ? CodexResult.Updated : CodexResult.Chained;
    }

    internal static bool UninstallCodex(string path) => UninstallCodex(path, out _);

    /// <summary>Removes Bellboard from `notify`, putting back the command it was passing notifications on to.</summary>
    internal static bool UninstallCodex(string path, out bool restored)
    {
        restored = false;
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllText(path).Split('\n').ToList();
        var i = TopLevelNotify(lines.ToArray());
        if (i < 0 || ParseTomlStrings(lines[i].TrimEnd('\r')) is not { } items || !IsOurNotify(items)) return false;
        if (items.Count > 4 && items[3] == ChainFlag)
        {
            lines[i] = FormatNotify(items.Skip(4)) + (lines[i].EndsWith('\r') ? "\r" : "");
            restored = true;
        }
        else
            lines.RemoveAt(i);
        File.WriteAllText(path, string.Join('\n', lines));
        return true;
    }

    internal const string ChainFlag = "--then";

    /// <summary>Splits `codex [--then &lt;command...&gt;] &lt;json&gt;` into the command to pass the notification on to, and the payload.</summary>
    internal static (string[]? Chain, string? Payload) ParseCodexArgs(string[] args)
    {
        var payload = args.Length > 1 ? args[^1] : null;
        return args.Length > 3 && args[1] == ChainFlag ? (args[2..^1], payload) : (null, payload);
    }

    /// <summary>Runs the notify command that was there before Bellboard, with the arguments Codex would have given it.</summary>
    private static void PassOn(string[] chain, string? payload)
    {
        try
        {
            var psi = new ProcessStartInfo(chain[0]) { UseShellExecute = false };
            foreach (var arg in chain[1..]) psi.ArgumentList.Add(arg);
            if (payload != null) psi.ArgumentList.Add(payload);
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            // Never let the other tool's failure break the hook (or the agent).
        }
    }

    private static bool IsOurNotify(List<string> items) =>
        items.Count >= 3 && items[1] == "hook" && items[2] == "codex"
        && Path.GetFileNameWithoutExtension(items[0].Replace('/', '\\').Split('\\')[^1]) is var name
        && (name.Equals("bell", StringComparison.OrdinalIgnoreCase) || name.Equals("wmux", StringComparison.OrdinalIgnoreCase));

    /// <summary>The `notify = ...` line before the first [table], or -1.</summary>
    private static int TopLevelNotify(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith('[')) return -1;
            if (Regex.IsMatch(t, @"^notify\s*=")) return i;
        }
        return -1;
    }

    private static string FormatNotify(IEnumerable<string> items) =>
        "notify = [" + string.Join(", ", items.Select(v => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    /// <summary>Reads a one-line TOML array of strings (`notify = ["a\\b", 'c']`); null for anything else.</summary>
    internal static List<string>? ParseTomlStrings(string line)
    {
        var i = line.IndexOf('=');
        if (i < 0) return null;
        i++;
        void Ws() { while (i < line.Length && line[i] is ' ' or '\t') i++; }
        Ws();
        if (i >= line.Length || line[i] != '[') return null;
        i++;
        var items = new List<string>();
        while (true)
        {
            Ws();
            if (i >= line.Length) return null;
            if (line[i] == ']') { i++; break; }
            var sb = new StringBuilder();
            if (line[i] == '\'')
            {
                var close = line.IndexOf('\'', i + 1);
                if (close < 0) return null;
                sb.Append(line, i + 1, close - i - 1);
                i = close + 1;
            }
            else if (line[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= line.Length) return null;
                    var c = line[i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= line.Length) return null;
                    var e = line[i++];
                    switch (e)
                    {
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u' when i + 4 <= line.Length && int.TryParse(line.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: return null;
                    }
                }
            }
            else return null;
            items.Add(sb.ToString());
            Ws();
            if (i < line.Length && line[i] == ',') { i++; continue; }
            if (i < line.Length && line[i] == ']') { i++; break; }
            return null;
        }
        Ws();
        return i >= line.Length || line[i] == '#' ? items : null;
    }
}
