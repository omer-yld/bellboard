using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Bellboard.Core;
using Bellboard.Shared;

namespace Bellboard.Services;

/// <summary>
/// Bellboard Remote on the app side: runs bellboard-remote.exe (the HTTPS/WebSocket server phones talk to) while
/// remote access is on, knows which devices are connected, and whether you're at the desk.
/// </summary>
public sealed class RemoteHost : IDisposable
{
    public const string SidecarExe = "bellboard-remote.exe";

    private readonly MuxController _mux;
    private readonly Dispatcher _ui;
    private Process? _process;
    private int _restarts;
    private DateTime _lastStart;
    private bool _disposed;
    private DateTime _lastActive = DateTime.Now;
    private bool _locked;

    public RemoteDevices Devices { get; }

    /// <summary>Process id of the sidecar this app started (0 when it isn't running).</summary>
    public int SidecarPid { get; private set; }

    /// <summary>Devices with a live connection right now (reported by the sidecar).</summary>
    public IReadOnlyList<string> Connected { get; private set; } = Array.Empty<string>();

    /// <summary>Why the sidecar isn't running, when it should be.</summary>
    public string? Problem { get; private set; }

    public event Action? StatusChanged;

    public RemoteHost(MuxController mux, Dispatcher ui, RemoteDevices? devices = null)
    {
        _mux = mux;
        _ui = ui;
        Devices = devices ?? new RemoteDevices(RemoteDevices.DefaultPath);
        Devices.Revoked += id => _mux.Events.Publish("remote", "revoked", new JsonObject { ["device"] = id });
        Devices.Changed += () => _ui.InvokeAsync(() => StatusChanged?.Invoke());
        try { SystemEvents.SessionSwitch += OnSessionSwitch; } catch { }
    }

    private RemoteConfig Config => _mux.Config.Remote;

    public bool Running => _process is { HasExited: false };

    /// <summary>The address phones open: the Tailscale/tunnel URL, the LAN address, or localhost.</summary>
    public string Url
    {
        get
        {
            if (Config.Mode == "lan") return $"https://{LanAddresses().FirstOrDefault() ?? "localhost"}:{Config.Port}";
            return Config.Url is { Length: > 0 } u ? u.TrimEnd('/') : $"http://127.0.0.1:{Config.Port}";
        }
    }

    /// <summary>Host names the sidecar answers to (anything else is refused, which stops DNS rebinding).</summary>
    public List<string> Hosts()
    {
        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };
        if (Config.Url is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var uri)) hosts.Add(uri.Host);
        if (Config.Mode == "lan")
        {
            hosts.AddRange(LanAddresses());
            hosts.Add(Environment.MachineName.ToLowerInvariant());
        }
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The exact origins (scheme, host and port) the phone app may be loaded from. Only requests from these may
    /// change anything: a page on another port of the same host (a dev server, another Tailscale Serve port) is
    /// "same-site" to the browser and would otherwise get the device cookie.
    /// </summary>
    public List<string> Origins()
    {
        var port = Config.Port;
        var list = new List<string> { $"http://127.0.0.1:{port}", $"http://localhost:{port}", $"http://[::1]:{port}" };
        if (Config.Url is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var uri)) list.Add(uri.GetLeftPart(UriPartial.Authority));
        if (Config.Mode == "lan")
            foreach (var h in LanAddresses().Append(Environment.MachineName.ToLowerInvariant()))
                list.Add($"https://{h}:{port}");
        return list.Select(o => o.ToLowerInvariant()).Distinct().ToList();
    }

    internal static IEnumerable<string> LanAddresses()
    {
        IEnumerable<string> list;
        try
        {
            list = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                            && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => a.Address.ToString())
                .ToList();
        }
        catch { list = Array.Empty<string>(); }
        return list;
    }

    // ---- at the desk or away ----

    /// <summary>Called when the main window is activated or deactivated.</summary>
    public void OnWindowActivity(bool active)
    {
        if (active)
        {
            _lastActive = DateTime.Now;
            // Back at the desk: questions waiting for the phone go back to the terminal's own dialog.
            if (Config.AnswerPrompts == "away" && _mux.Prompts.Pending.Count > 0)
            {
                Log.Info("back at the desk: handing pending permission requests back to the terminal");
                _mux.Prompts.CancelAll();
            }
        }
        else _lastActive = DateTime.Now;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) _locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            _locked = false;
            _lastActive = DateTime.Now;
        }
    }

    /// <summary>The screen is locked, or Bellboard hasn't been in the foreground for "awayMinutes".</summary>
    public bool IsAway => _locked || (!_mux.Window.IsActive && DateTime.Now - _lastActive > TimeSpan.FromMinutes(Math.Max(0, Config.AwayMinutes)));

    /// <summary>Whether a Claude Code permission request should wait for the phone.</summary>
    public bool ShouldRoutePrompts =>
        Config.Enabled && Running
        && Devices.Devices.Any(d => RemotePolicy.Rank(d.Level) >= RemotePolicy.Rank(RemotePolicy.Respond))
        && Config.AnswerPrompts switch { "always" => true, "away" => IsAway, _ => false };

    /// <summary>"Phone fit" resizes a shell to the phone only while nobody is typing at the desk.</summary>
    public bool DeskIdle => _locked || (!_mux.Window.IsActive && DateTime.Now - _lastActive > TimeSpan.FromSeconds(60));

    // ---- the sidecar process ----

    /// <summary>Start or stop the sidecar to match the settings (called at startup and after settings change).</summary>
    public void Apply()
    {
        if (_disposed) return;
        if (!Config.Enabled)
        {
            Stop();
            Problem = null;
            StatusChanged?.Invoke();
            return;
        }
        if (Running && _startedWith == Signature()) return;
        Stop();
        Start();
    }

    private string? _startedWith;
    private string Signature() => $"{Config.Mode}|{Config.Port}|{Config.Url}|{Config.Push}|{Config.PushText}";

    private void Start()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, SidecarExe);
        if (!File.Exists(exe))
        {
            Problem = $"{SidecarExe} is missing next to Bellboard.exe";
            Log.Error(Problem);
            StatusChanged?.Invoke();
            return;
        }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment[BellboardProtocol.EnvPipe] = _mux.PipeName;
        psi.Environment["BELLBOARD_PARENT_PID"] = Environment.ProcessId.ToString();
        try
        {
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Log.Info("remote: " + line); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Log.Error("remote: " + line); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.Exited += (_, _) => _ui.InvokeAsync(() => OnExited(p));
            try { Bellboard.Terminal.ProcessJob.Add(p.Handle); } catch { }
            _process = p;
            SidecarPid = p.Id;
            _startedWith = Signature();
            _lastStart = DateTime.Now;
            Problem = null;
            Log.Info($"remote sidecar started (pid {p.Id}, {Config.Mode}, port {Config.Port})");
        }
        catch (Exception ex)
        {
            Problem = "couldn't start the remote server: " + ex.Message;
            Log.Error(Problem, ex);
        }
        StatusChanged?.Invoke();
    }

    private void OnExited(Process p)
    {
        if (p != _process) return;
        var code = SafeExitCode(p);
        _process = null;
        SidecarPid = 0;
        Connected = Array.Empty<string>();
        StatusChanged?.Invoke();
        if (_disposed || !Config.Enabled) return;
        if (DateTime.Now - _lastStart > TimeSpan.FromMinutes(2)) _restarts = 0;
        var delay = new[] { 1, 2, 5, 10, 30 }[Math.Min(_restarts, 4)];
        _restarts++;
        Problem = code == 3 ? $"port {Config.Port} is in use (change it in Settings › Remote)" : $"the remote server stopped (exit code {code}); restarting";
        Log.Error($"remote sidecar exited with {code}; restarting in {delay}s");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed && Config.Enabled && !Running) Start();
        };
        timer.Start();
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private void Stop()
    {
        var p = _process;
        _process = null;
        SidecarPid = 0;
        Connected = Array.Empty<string>();
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        p.Dispose();
        Log.Info("remote sidecar stopped");
    }

    /// <summary>The sidecar reports which devices have a socket open.</summary>
    public void SetConnected(IEnumerable<string> devices)
    {
        Connected = devices.Distinct().ToList();
        StatusChanged?.Invoke();
    }

    /// <summary>What the sidecar needs to start (remote.hello).</summary>
    public JsonObject SidecarConfig()
    {
        var o = new JsonObject
        {
            ["port"] = Config.Port,
            ["bind"] = Config.Mode == "lan" ? "0.0.0.0" : "127.0.0.1",
            ["hosts"] = new JsonArray(Hosts().Select(h => (JsonNode)h).ToArray()),
            ["origins"] = new JsonArray(Origins().Select(o => (JsonNode)o).ToArray()),
            ["push"] = Config.Push,
            ["pushText"] = Config.PushText,
            ["version"] = BellboardProtocol.Version,
            ["subject"] = "https://github.com/omer-yld/bellboard",
        };
        if (Config.Push)
        {
            var (pub, priv) = Devices.VapidKeys(GenerateVapid);
            o["vapidPublic"] = pub;
            o["vapidPrivate"] = priv;
        }
        if (Config.Mode == "lan")
        {
            var (pfx, _) = Devices.Certificate(() => CreateCertificate(Hosts()));
            o["certPfx"] = Convert.ToBase64String(pfx);
        }
        return o;
    }

    private static (string, string) GenerateVapid()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 4;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);
        return (RemoteDevices.Base64Url(pub), RemoteDevices.Base64Url(p.D!));
    }

    /// <summary>LAN mode: a self-signed certificate for this PC's names and addresses (10 years).</summary>
    internal static (byte[] Pfx, string Fingerprint) CreateCertificate(IEnumerable<string> hosts)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=Bellboard Remote ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var h in hosts)
        {
            var host = h.Trim('[', ']');
            if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(host);
        }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pfx);
        var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
        return (pfx, string.Join(":", Enumerable.Range(0, fp.Length / 2).Select(i => fp.Substring(i * 2, 2))));
    }

    public string? CertificateFingerprint => Devices.CertificateFingerprint;

    public void Dispose()
    {
        _disposed = true;
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        Stop();
    }
}
