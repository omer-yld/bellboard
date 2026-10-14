using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Bellboard.Services;

/// <summary>
/// Tailscale, the recommended way to reach Bellboard Remote from a phone: `tailscale serve` puts the sidecar
/// (listening on 127.0.0.1 only) at https://&lt;pc&gt;.&lt;tailnet&gt;.ts.net with a real certificate, reachable only
/// from your own tailnet devices.
/// </summary>
public static class Tailscale
{
    public sealed record Status(bool Installed, bool Running, string? DnsName, string? Error)
    {
        public string? Url => DnsName is { } d ? "https://" + d : null;
    }

    public static string? FindExe()
    {
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var p = Path.Combine(dir, "Tailscale", "tailscale.exe");
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "tailscale.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static async Task<Status> GetStatusAsync()
    {
        var exe = FindExe();
        if (exe == null) return new Status(false, false, null, "Tailscale isn't installed");
        var (code, output, error) = await RunAsync(exe, "status --json", 8000);
        if (code != 0) return new Status(true, false, null, Trim(error) ?? Trim(output) ?? "tailscale status failed");
        try
        {
            var json = JsonNode.Parse(output) as JsonObject;
            var state = (string?)json?["BackendState"];
            var dns = ((string?)json?["Self"]?["DNSName"])?.TrimEnd('.');
            if (state != "Running") return new Status(true, false, dns, $"Tailscale is {state?.ToLowerInvariant() ?? "not running"}");
            return new Status(true, true, string.IsNullOrEmpty(dns) ? null : dns, null);
        }
        catch (Exception ex) { return new Status(true, false, null, ex.Message); }
    }

    /// <summary>`tailscale serve --bg --https=443 http://127.0.0.1:port`. Returns an error message, or null.</summary>
    public static async Task<string?> ServeAsync(int port)
    {
        var exe = FindExe();
        if (exe == null) return "Tailscale isn't installed";
        var (code, output, error) = await RunAsync(exe, $"serve --bg --yes --https=443 http://127.0.0.1:{port}", 30000);
        if (code == 0) return null;
        var msg = Trim(error) ?? Trim(output) ?? $"tailscale serve exited with {code}";
        // Older clients don't know --yes.
        if (msg.Contains("--yes", StringComparison.OrdinalIgnoreCase) || msg.Contains("flag provided but not defined", StringComparison.OrdinalIgnoreCase))
        {
            (code, output, error) = await RunAsync(exe, $"serve --bg --https=443 http://127.0.0.1:{port}", 30000);
            if (code == 0) return null;
            msg = Trim(error) ?? Trim(output) ?? $"tailscale serve exited with {code}";
        }
        return msg;
    }

    public static async Task<string?> StopServingAsync()
    {
        var exe = FindExe();
        if (exe == null) return null;
        var (code, output, error) = await RunAsync(exe, "serve --https=443 off", 15000);
        return code == 0 ? null : Trim(error) ?? Trim(output);
    }

    public static string ServeCommand(int port) => $"tailscale serve --bg --https=443 http://127.0.0.1:{port}";

    private static string? Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<(int Code, string Out, string Err)> RunAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            var exited = p.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(timeoutMs)) != exited)
            {
                try { p.Kill(true); } catch { }
                return (-1, "", "tailscale didn't answer in time");
            }
            return (p.ExitCode, await o, await e);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
