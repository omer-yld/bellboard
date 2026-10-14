using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Bellboard.Core;
using Bellboard.Services;
using Bellboard.Shared;

namespace Bellboard.Ipc;

/// <summary>
/// The `remote.*` pipe methods: those only bellboard-remote may call (pairing, auth, push targets), and those
/// behind `bell remote …` (status, pairing codes, devices, Tailscale setup).
/// </summary>
public sealed class RemoteCommands
{
    private readonly MuxController _mux;

    public RemoteCommands(MuxController mux) => _mux = mux;

    private RemoteHost Host => _mux.Remote;
    private RemoteDevices Devices => _mux.Remote.Devices;

    public static bool IsSidecarMethod(string method) => method is "remote.hello" or "remote.pair" or "remote.auth" or "remote.logout"
        or "remote.connected" or "remote.push.subscribe" or "remote.push.unsubscribe" or "remote.push.targets" or "remote.push.gone";

    /// <summary>Every action a phone takes that changes something, in %LOCALAPPDATA%\Bellboard\remote.log.</summary>
    public static void Audit(string text)
    {
        try
        {
            var path = Path.Combine(BellboardProtocol.LocalDataDir, "remote.log");
            Directory.CreateDirectory(BellboardProtocol.LocalDataDir);
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static JsonObject DeviceJson(RemoteDevice d) => new() { ["id"] = d.Id, ["name"] = d.Name, ["level"] = d.Level };

    public Task<JsonNode?> SidecarAsync(string method, JsonObject p)
    {
        JsonNode? result;
        switch (method)
        {
            case "remote.hello":
                result = Host.SidecarConfig();
                break;
            case "remote.pair":
                try
                {
                    var (device, token) = Devices.Pair(Str(p, "code"), Str(p, "name"), Str(p, "ip"));
                    Audit($"paired {device.Name} ({device.Id}) from {Str(p, "ip") ?? "?"}");
                    Log.Info($"remote: paired {device.Name} ({device.Id})");
                    if ((_mux.Selected?.FocusedPane ?? _mux.Workspaces.SelectMany(w => w.Panes).FirstOrDefault()) is { } anchor)
                        _mux.Notifications.Post(anchor, "Bellboard Remote", $"{device.Name} was paired. Settings › Remote can revoke it.", "remote");
                    result = new JsonObject { ["ok"] = true, ["device"] = DeviceJson(device), ["token"] = token };
                }
                catch (RemoteAuthException ex)
                {
                    Audit($"pairing refused from {Str(p, "ip") ?? "?"}: {ex.Message}");
                    result = new JsonObject { ["ok"] = false, ["error"] = ex.Message, ["status"] = ex.Status };
                }
                break;
            case "remote.auth":
                result = Devices.Authenticate(Str(p, "token"), Str(p, "ip")) is { } d ? DeviceJson(d) : null;
                break;
            case "remote.logout":
                if (Str(p, "device") is { } gone && Devices.Find(gone) is { } dev)
                {
                    Audit($"{dev.Name} ({dev.Id}) signed out");
                    Devices.Revoke(gone);
                }
                result = true;
                break;
            case "remote.connected":
                Host.SetConnected((p["devices"] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0) ?? []);
                result = true;
                break;
            case "remote.push.subscribe":
                Devices.SetPush(Str(p, "device") ?? "", p["subscription"]?.DeepClone() as JsonObject);
                result = true;
                break;
            case "remote.push.unsubscribe":
            case "remote.push.gone":
                if (Devices.Find(Str(p, "device")) != null) Devices.SetPush(Str(p, "device")!, null);
                result = true;
                break;
            case "remote.push.targets":
                result = new JsonArray(Devices.Devices.Where(x => x.Push != null)
                    .Select(x => (JsonNode)new JsonObject { ["device"] = x.Id, ["subscription"] = x.Push!.DeepClone() }).ToArray());
                break;
            default:
                throw new InvalidOperationException($"unknown method '{method}'");
        }
        return Task.FromResult(result);
    }

    public async Task<JsonNode?> LocalAsync(string method, JsonObject p)
    {
        var cfg = _mux.Config.Remote;
        switch (method)
        {
            case "remote.status":
                return Status();
            case "remote.enable":
            case "remote.disable":
            {
                var updated = _mux.Config.Clone();
                updated.Remote.Enabled = method == "remote.enable";
                if (Str(p, "mode") is { } mode)
                {
                    if (mode is not ("tailscale" or "local" or "lan")) throw new InvalidOperationException("mode must be tailscale, local or lan");
                    updated.Remote.Mode = mode;
                }
                if (p["port"] is JsonValue pv && pv.TryGetValue<int>(out var port)) updated.Remote.Port = port;
                if (Str(p, "url") is { } url) updated.Remote.Url = url == "none" ? null : url;
                updated.Save();
                _mux.ApplyConfig(updated);
                // The sidecar takes a moment to listen; report what we know now.
                return Status();
            }
            case "remote.set":
            {
                var updated = _mux.Config.Clone();
                var r = updated.Remote;
                if (Str(p, "answerPrompts") is { } ap)
                {
                    if (ap is not ("away" or "always" or "off")) throw new InvalidOperationException("answerPrompts must be away, always or off");
                    r.AnswerPrompts = ap;
                }
                if (p["push"] is JsonValue push && push.TryGetValue<bool>(out var pushOn)) r.Push = pushOn;
                if (p["pushText"] is JsonValue pt && pt.TryGetValue<bool>(out var ptOn)) r.PushText = ptOn;
                if (p["awayMinutes"] is JsonValue am && am.TryGetValue<int>(out var minutes)) r.AwayMinutes = Math.Clamp(minutes, 0, 1440);
                updated.Save();
                _mux.ApplyConfig(updated);
                return Status();
            }
            case "remote.pair-code":
            {
                if (!cfg.Enabled) throw new InvalidOperationException("remote access is off: `bell remote enable` first");
                var code = Devices.CreateCode();
                var url = $"{Host.Url}/#pair={code}";
                return new JsonObject
                {
                    ["code"] = code,
                    ["url"] = url,
                    ["expires"] = Devices.CodeExpires?.ToString("o"),
                    ["qr"] = QrCode.ToText(url),
                };
            }
            case "remote.devices":
                return new JsonArray(Devices.Devices.Select(d => (JsonNode)new JsonObject
                {
                    ["id"] = d.Id, ["name"] = d.Name, ["level"] = d.Level, ["paired"] = d.Paired.ToString("o"),
                    ["lastSeen"] = d.LastSeen?.ToString("o"), ["lastIp"] = d.LastIp, ["push"] = d.Push != null,
                    ["connected"] = Host.Connected.Contains(d.Id),
                }).ToArray());
            case "remote.revoke":
            {
                var id = Str(p, "device") ?? throw new InvalidOperationException("which device? (see `bell remote devices`)");
                var name = Devices.Find(id)?.Name;
                if (!Devices.Revoke(id)) throw new InvalidOperationException($"no device '{id}'");
                Audit($"revoked {name} ({id}) from the CLI");
                return true;
            }
            case "remote.level":
                Devices.SetLevel(Str(p, "device") ?? "", Str(p, "level") ?? "");
                Audit($"{Str(p, "device")} is now {Str(p, "level")}");
                return true;
            case "remote.rename":
                Devices.Rename(Str(p, "device") ?? "", Str(p, "name") ?? "");
                return true;
            case "remote.tailscale":
            {
                var st = await Tailscale.GetStatusAsync();
                if (!st.Installed) throw new InvalidOperationException("Tailscale isn't installed. Get it from https://tailscale.com/download, sign in, then run this again.");
                if (!st.Running || st.Url == null) throw new InvalidOperationException((st.Error ?? "Tailscale isn't connected") + ". Sign in to Tailscale, then run this again.");
                if (p["off"] is JsonValue off && off.TryGetValue<bool>(out var isOff) && isOff)
                {
                    var stopErr = await Tailscale.StopServingAsync(cfg.Port);
                    if (stopErr != null) throw new InvalidOperationException(stopErr);
                    return new JsonObject { ["served"] = false };
                }
                var err = await Tailscale.ServeAsync(cfg.Port);
                if (err != null)
                    throw new InvalidOperationException($"tailscale serve failed: {err}\nIf HTTPS certificates are off for your tailnet, turn them on at https://login.tailscale.com/admin/dns and try again.");
                var url = Tailscale.UrlFor(st.DnsName!, cfg.Port);
                var updated = _mux.Config.Clone();
                updated.Remote.Enabled = true;
                updated.Remote.Mode = "tailscale";
                updated.Remote.Url = url;
                updated.Save();
                _mux.ApplyConfig(updated);
                return new JsonObject { ["served"] = true, ["url"] = url };
            }
        }
        throw new InvalidOperationException($"unknown method '{method}'");
    }

    private JsonObject Status() => new()
    {
        ["enabled"] = _mux.Config.Remote.Enabled,
        ["running"] = Host.Running,
        ["problem"] = Host.Problem,
        ["mode"] = _mux.Config.Remote.Mode,
        ["port"] = _mux.Config.Remote.Port,
        ["url"] = Host.Url,
        ["devices"] = Devices.Devices.Count,
        ["connected"] = new JsonArray(Host.Connected.Select(id => (JsonNode)(Devices.Find(id)?.Name ?? id)).ToArray()),
        ["answerPrompts"] = _mux.Config.Remote.AnswerPrompts,
        ["away"] = Host.IsAway,
        ["push"] = _mux.Config.Remote.Push,
        ["pushText"] = _mux.Config.Remote.PushText,
        ["fingerprint"] = _mux.Config.Remote.Mode == "lan" ? Host.CertificateFingerprint : null,
    };
}
