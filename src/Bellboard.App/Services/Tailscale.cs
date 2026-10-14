using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// The address phones open: https://&lt;pc&gt;.&lt;tailnet&gt;.ts.net:&lt;port&gt;. Bellboard publishes on its own port (the same
    /// number it listens on locally), never 443: that's where people already serve their own apps, and
    /// `tailscale serve` would replace them.
    /// </summary>
    public static string UrlFor(string dnsName, int port) => port == 443 ? $"https://{dnsName}" : $"https://{dnsName}:{port}";

    public static string ServeCommand(int port) => $"tailscale serve --bg --https={port} http://127.0.0.1:{port}";

    /// <summary>What `tailscale serve` publishes now for this machine: tailnet HTTPS port → local target of "/".</summary>
    internal static Dictionary<int, string?> ParseServe(string json)
    {
        var map = new Dictionary<int, string?>();
        if (JsonNode.Parse(json) is not JsonObject root) return map;
        if (root["TCP"] is JsonObject tcp)
            foreach (var kv in tcp)
                if (int.TryParse(kv.Key, out var port)) map[port] = kv.Value?["TCPForward"] is { } fwd ? "tcp://" + (string?)fwd : null;
        if (root["Web"] is JsonObject web)
            foreach (var kv in web)
            {
                var hostPort = kv.Key;
                var colon = hostPort.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(hostPort[(colon + 1)..], out var port)) continue;
                var handlers = kv.Value?["Handlers"] as JsonObject;
                map[port] = (string?)handlers?["/"]?["Proxy"] ?? (handlers is { Count: > 0 } ? "(paths: " + string.Join(", ", handlers.Select(h => h.Key)) + ")" : null);
            }
        return map;
    }

    private static async Task<(Dictionary<int, string?>? Map, string? Error)> ServeStatusAsync(string exe)
    {
        var (code, output, error) = await RunAsync(exe, "serve status --json", 15000);
        if (code != 0) return (null, Trim(error) ?? Trim(output) ?? "tailscale serve status failed");
        if (string.IsNullOrWhiteSpace(output)) return (new Dictionary<int, string?>(), null); // nothing served
        try { return (ParseServe(output), null); }
        catch (Exception ex) { return (null, "couldn't read tailscale serve status: " + ex.Message); }
    }

    private static bool IsOurs(string? target, int port) =>
        target != null && (target.TrimEnd('/').Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                           || target.TrimEnd('/').Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// `tailscale serve --bg --https=port http://127.0.0.1:port`, but only if that tailnet port is free or already ours:
    /// whatever else you publish with `tailscale serve` is left alone. Returns an error message, or null.
    /// </summary>
    public static async Task<string?> ServeAsync(int port)
    {
        var exe = FindExe();
        if (exe == null) return "Tailscale isn't installed";
        var (map, statusError) = await ServeStatusAsync(exe);
        if (map == null) return statusError;
        if (map.TryGetValue(port, out var existing))
        {
            if (IsOurs(existing, port)) return null;
            return $"port {port} on your tailnet is already used by `tailscale serve` ({existing ?? "another service"}); " +
                   "Bellboard won't replace it. Pick another port (bell remote enable --port N), then run this again.";
        }
        var args = $"serve --bg --yes --https={port} http://127.0.0.1:{port}";
        var (code, output, error) = await RunAsync(exe, args, 30000);
        if (code == 0) return null;
        var msg = Trim(error) ?? Trim(output) ?? $"tailscale serve exited with {code}";
        // Older clients don't know --yes.
        if (msg.Contains("--yes", StringComparison.OrdinalIgnoreCase) || msg.Contains("flag provided but not defined", StringComparison.OrdinalIgnoreCase))
        {
            (code, output, error) = await RunAsync(exe, args.Replace(" --yes", ""), 30000);
            if (code == 0) return null;
            msg = Trim(error) ?? Trim(output) ?? $"tailscale serve exited with {code}";
        }
        return msg;
    }

    /// <summary>Stops publishing Bellboard's port, only if it points at Bellboard. Returns an error message, or null.</summary>
    public static async Task<string?> StopServingAsync(int port)
    {
        var exe = FindExe();
        if (exe == null) return null;
        var (map, statusError) = await ServeStatusAsync(exe);
        if (map == null) return statusError;
        if (!map.TryGetValue(port, out var existing)) return null; // not published
        if (!IsOurs(existing, port)) return $"port {port} on your tailnet serves {existing ?? "something else"}, not Bellboard; left it alone";
        var (code, output, error) = await RunAsync(exe, $"serve --https={port} off", 15000);
        return code == 0 ? null : Trim(error) ?? Trim(output);
    }

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
