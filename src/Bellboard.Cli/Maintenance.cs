using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bellboard.Shared;

namespace Bellboard.Cli;

/// <summary>`bell update` and `bell doctor`.</summary>
internal static class Maintenance
{
    private static string DataDir => BellboardProtocol.LocalDataDir;
    private static string ConfigDir => BellboardProtocol.RoamingDataDir;
    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    private static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');

    private static JsonObject? ReadConfig()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonNode.Parse(File.ReadAllText(ConfigPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : null;
        }
        catch { return null; }
    }

    // ---- update ----

    public static int Update(Args a)
    {
        // Only when you run it: Bellboard itself makes no network requests unless config.json sets "updateFeed".
        var feed = a.Opt("feed") ?? (string?)ReadConfig()?["updateFeed"] ?? BellboardProtocol.DefaultUpdateFeed;
        var info = JsonNode.Parse(ReadText(feed)) as JsonObject ?? throw new BellboardException("update feed is not JSON");
        var latest = (string?)info["version"] ?? throw new BellboardException("update feed has no version");
        var current = BellboardProtocol.Version;
        bool newer = Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv) && lv > cv;
        Console.WriteLine($"installed {current}, latest {latest}");
        if (!newer) { Console.WriteLine("Bellboard is up to date"); return 0; }
        if (a.Has("check")) { Console.WriteLine("an update is available: run `bell update`"); return 10; }

        var url = (string?)info["url"] ?? throw new BellboardException("update feed has no url");
        var sha = ((string?)info["sha256"] ?? throw new BellboardException("update feed has no sha256")).ToLowerInvariant();
        url = Resolve(feed, url);

        var stage = Path.Combine(Path.GetTempPath(), $"bellboard-update-{latest}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var zip = Path.Combine(stage, "bellboard.zip");
        Console.WriteLine($"downloading {url}");
        File.WriteAllBytes(zip, ReadBytes(url));
        using (var fs = File.OpenRead(zip))
        {
            var actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (actual != sha) throw new BellboardException($"checksum mismatch (expected {sha}, got {actual}); not installing");
        }
        var files = Path.Combine(stage, "files");
        ZipFile.ExtractToDirectory(zip, files);
        if (!File.Exists(Path.Combine(files, "Bellboard.exe"))) throw new BellboardException("the update package has no Bellboard.exe");
        Console.WriteLine("checksum ok");

        if (!a.Has("yes"))
        {
            Console.Write($"Install {latest} into {InstallDir}? Bellboard restarts and restores your workspaces. [y/N] ");
            if (!(Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("cancelled"); return 1; }
        }

        // Quit the running app (it saves the session), then swap files after this CLI exits too.
        bool wasRunning = false;
        using (var client = PipeClient.TryConnect(BellboardProtocol.ServerPipeName(), 500))
        {
            if (client != null)
            {
                wasRunning = true;
                try { client.Call("app.quit"); } catch { }
            }
        }
        var script = Path.Combine(stage, "apply.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            $install = '{{InstallDir.Replace("'", "''")}}'
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Process Bellboard, bell, bellboard-remote -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase) }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
            Copy-Item '{{files.Replace("'", "''")}}\*' $install -Recurse -Force
            if ({{(wasRunning ? "$true" : "$false")}}) { Start-Process (Join-Path $install 'Bellboard.exe') }
            Start-Sleep -Seconds 1
            Remove-Item '{{stage.Replace("'", "''")}}' -Recurse -Force -ErrorAction SilentlyContinue
            """);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Console.WriteLine($"updating to {latest}{(wasRunning ? "; Bellboard will restart" : "")}");
        return 0;
    }

    private static string Resolve(string feed, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _) || Path.IsPathRooted(url)) return url;
        if (Uri.TryCreate(feed, UriKind.Absolute, out var f) && f.Scheme.StartsWith("http")) return new Uri(f, url).ToString();
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(feed))!, url);
    }

    private static string ReadText(string location) => Encoding.UTF8.GetString(ReadBytes(location)).TrimStart('\uFEFF');

    private static byte[] ReadBytes(string location)
    {
        if (location.StartsWith("http://") || location.StartsWith("https://"))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Bellboard/{BellboardProtocol.Version}");
            return http.GetByteArrayAsync(location).GetAwaiter().GetResult();
        }
        var path = location.StartsWith("file://") ? new Uri(location).LocalPath : location;
        return File.ReadAllBytes(path);
    }

    // ---- doctor ----

    public static int Doctor(Args a)
    {
        var r = new StringBuilder();
        void Line(string k, object? v) => r.AppendLine($"  {k,-22} {v}");
        void Section(string s) => r.AppendLine().AppendLine(s);

        r.AppendLine($"Bellboard doctor — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Section("install");
        Line("Bellboard", BellboardProtocol.Version);
        Line("folder", InstallDir);
        Line("runtime", RuntimeInformation.FrameworkDescription + (File.Exists(Path.Combine(InstallDir, "coreclr.dll")) ? " (bundled)" : " (shared)"));
        Line("windows", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line("webview2 runtime", WebView2Version() ?? "NOT FOUND (install the Microsoft Edge WebView2 Runtime)");
        Line("conpty", File.Exists(Path.Combine(InstallDir, "conpty.dll")) && File.Exists(Path.Combine(InstallDir, "x64", "OpenConsole.exe")) ? "bundled OpenConsole" : "inbox (conpty.dll/OpenConsole missing)");

        Section("running instance");
        using (var client = PipeClient.TryConnect(BellboardProtocol.ServerPipeName(), 800))
        {
            if (client == null) Line("status", $"not running (pipe {BellboardProtocol.ServerPipeName()})");
            else
            {
                try
                {
                    var p = client.Call("system.ping")!;
                    Line("status", $"running, pid {p["pid"]}, {p["workspaces"]} workspace(s)");
                    Line("webview2 (in use)", p["webview2"]);
                    Line("windows toasts", p["toasts"]);
                }
                catch (Exception ex) { Line("status", "pipe error: " + ex.Message); }
            }
        }

        Section("settings");
        var cfg = ReadConfig();
        Line("config", File.Exists(ConfigPath) ? ConfigPath + (cfg == null ? "  (INVALID JSON — defaults in use)" : "") : ConfigPath + " (not created yet)");
        if (cfg != null)
            foreach (var k in new[] { "shell", "fontFamily", "fontSize", "conpty", "prestartShell", "unloadIdleWorkspacesMinutes", "restoreSession", "desktopNotifications", "updateFeed" })
                Line(k, cfg[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : cfg[k]?.ToJsonString() ?? "(default)");
        Line("data", $"{DataDir} ({SizeOf(DataDir)})");

        Section("shells");
        foreach (var (name, path) in new[] { ("pwsh", Find("pwsh.exe")), ("powershell", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
                                            ("cmd", Path.Combine(Environment.SystemDirectory, "cmd.exe")), ("git bash", GitBash()), ("wsl", Find("wsl.exe")) })
            Line(name, path != null && File.Exists(path) ? path : "-");

        Section("agents");
        var claudeSettings = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cd ? cd : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");
        Line("claude code", Find("claude.exe") ?? Find("claude.cmd") ?? "-");
        Line("claude hooks", File.Exists(claudeSettings) && Hooks.IsClaudeInstalled(claudeSettings) ? "installed" : "not installed (bell hooks install)");
        var claudeJson = Hooks.ClaudeJsonPath();
        Line("claude mcp", File.Exists(claudeJson) && File.ReadAllText(claudeJson).Contains($"\"{Hooks.McpName}\"") ? "registered" : "not registered (bell mcp install)");
        Line("Bellboard skill", File.Exists(Path.Combine(Path.GetDirectoryName(claudeSettings)!, "skills", "bellboard", "SKILL.md")) ? "installed" : "not installed (bell skill install)");
        Line("codex", Find("codex.exe") ?? Find("codex.cmd") ?? "-");

        Section("recent errors");
        var log = Path.Combine(DataDir, "bellboard.log");
        var errors = File.Exists(log) ? ReadShared(log).Split('\n').Where(l => l.Contains("[ERROR]")).TakeLast(10).ToList() : new List<string>();
        if (errors.Count == 0) r.AppendLine("  none");
        foreach (var e in errors) r.AppendLine("  " + (e.Length > 220 ? e[..220] + "…" : e.TrimEnd()));

        Console.WriteLine(r.ToString());
        if (a.Has("zip"))
        {
            var zipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"bellboard-doctor-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            Add(zip, "report.txt", r.ToString());
            if (File.Exists(ConfigPath)) Add(zip, "config.json", ReadShared(ConfigPath));
            if (File.Exists(log)) Add(zip, "bellboard.log", ReadShared(log));
            // session.json holds scrollback (possibly secrets) — only with --include-session.
            var session = Path.Combine(DataDir, "session.json");
            if (a.Has("include-session") && File.Exists(session)) Add(zip, "session.json", ReadShared(session));
            Console.WriteLine($"wrote {zipPath} (report, config, log{(a.Has("include-session") ? ", session" : "")})");
        }
        return 0;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string SizeOf(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "missing";
            long bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } });
            return $"{bytes / 1024.0 / 1024:0.#} MB";
        }
        catch { return "?"; }
    }

    private static string? Find(string exe) => Terminal.ShellResolverLite.FindOnPath(exe);

    private static string? GitBash()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(p) ? p : null;
    }

    private static string? WebView2Version()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" })
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    if (hive.OpenSubKey(key)?.GetValue("pv") is string v && v.Length > 0 && v != "0.0.0.0") return v;
                }
                catch { }
            }
        }
        return null;
    }
}
