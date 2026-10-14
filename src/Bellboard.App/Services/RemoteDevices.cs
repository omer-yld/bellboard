using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bellboard.Shared;

namespace Bellboard.Services;

/// <summary>A phone or browser paired with Bellboard Remote.</summary>
public sealed class RemoteDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Level { get; set; } = RemotePolicy.Respond;
    /// <summary>SHA-256 of the device token (the token itself is only ever on the device).</summary>
    public string TokenHash { get; set; } = "";
    public DateTime Paired { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? LastIp { get; set; }
    /// <summary>The browser's PushSubscription.toJSON(), when the device turned notifications on.</summary>
    public JsonObject? Push { get; set; }
}

/// <summary>
/// Paired devices, pairing codes and the keys Bellboard Remote needs, in %LOCALAPPDATA%\Bellboard\remote.dat
/// encrypted with DPAPI for the current Windows user. Lives in the app (not the sidecar) so the app decides who
/// may do what, and revoking a device is immediate.
/// </summary>
public sealed class RemoteDevices
{
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    internal const int MaxFailures = 5;
    internal static readonly TimeSpan Lockout = TimeSpan.FromMinutes(10);

    private sealed class Store
    {
        public List<RemoteDevice> Devices { get; set; } = new();
        public string? VapidPublic { get; set; }
        public string? VapidPrivate { get; set; }
        /// <summary>LAN mode's self-signed certificate (PFX, base64) and its SHA-256 fingerprint.</summary>
        public string? CertPfx { get; set; }
        public string? CertFingerprint { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly string? _path;
    private readonly Func<DateTime> _now;
    private Store _store = new();
    private (string Code, DateTime Expires)? _code;
    private readonly List<DateTime> _failures = new();
    private DateTime _lockedUntil;

    /// <param name="path">null keeps everything in memory (tests).</param>
    public RemoteDevices(string? path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
        Load();
    }

    public static string DefaultPath => Path.Combine(BellboardProtocol.LocalDataDir, "remote.dat");

    public event Action? Changed;
    /// <summary>A device was removed: the sidecar closes its sockets.</summary>
    public event Action<string>? Revoked;

    public IReadOnlyList<RemoteDevice> Devices => _store.Devices;

    public RemoteDevice? Find(string? id) => _store.Devices.FirstOrDefault(d => d.Id == id);

    // ---- pairing ----

    /// <summary>A new one-time code (replaces any earlier one), shown as XXXX-XXXX.</summary>
    public string CreateCode()
    {
        var chars = new char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        var code = new string(chars);
        _code = (code, _now() + CodeLifetime);
        return code[..4] + "-" + code[4..];
    }

    public DateTime? CodeExpires => _code is { } c && c.Expires > _now() ? c.Expires : null;

    public void CancelCode() => _code = null;

    /// <summary>Exchanges a pairing code for a device and its token. Throws with a message for the phone.</summary>
    public (RemoteDevice Device, string Token) Pair(string? code, string? name, string? ip)
    {
        var now = _now();
        if (now < _lockedUntil)
            throw new RemoteAuthException("Too many wrong codes. Wait a few minutes and make a new code on the desktop.", 429);
        var given = Normalize(code);
        // Without a live code there's nothing to guess: say so, and don't let strangers lock pairing for you.
        if (_code is not { } live || live.Expires <= now)
            throw new RemoteAuthException(_code != null ? "That code expired. Make a new one on the desktop." : "No pairing code is active. Make one on the desktop.", 400);
        if (_code is not { } c || given.Length != 8
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(c.Code)))
        {
            _failures.RemoveAll(f => now - f > Lockout);
            _failures.Add(now);
            if (_failures.Count >= MaxFailures)
            {
                _lockedUntil = now + Lockout;
                _code = null;
                _failures.Clear();
                Log.Info("remote pairing locked after repeated wrong codes");
            }
            throw new RemoteAuthException("That code isn't right.", 400);
        }
        _code = null;
        _failures.Clear();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new RemoteDevice
        {
            Id = "d" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Name = CleanName(name),
            Level = RemotePolicy.Respond,
            TokenHash = Hash(token),
            Paired = now,
            LastSeen = now,
            LastIp = ip,
        };
        _store.Devices.Add(device);
        Save();
        return (device, token);
    }

    public RemoteDevice? Authenticate(string? token, string? ip)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var hash = Hash(token);
        var d = _store.Devices.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.TokenHash), Encoding.ASCII.GetBytes(hash)));
        if (d == null) return null;
        var now = _now();
        // "Last seen" is for the device list; don't rewrite the file on every request.
        if (d.LastSeen is not { } seen || now - seen > TimeSpan.FromMinutes(1) || d.LastIp != ip)
        {
            d.LastSeen = now;
            if (ip != null) d.LastIp = ip;
            Save();
        }
        return d;
    }

    public bool Revoke(string id)
    {
        var d = Find(id);
        if (d == null) return false;
        _store.Devices.Remove(d);
        Save();
        Revoked?.Invoke(id);
        return true;
    }

    public void SetLevel(string id, string level)
    {
        if (!RemotePolicy.IsLevel(level)) throw new InvalidOperationException("level must be view, respond or full");
        var d = Find(id) ?? throw new InvalidOperationException($"no device '{id}'");
        d.Level = level;
        Save();
        // Sockets carry the level they authenticated with: reconnect the device so the new level applies.
        Revoked?.Invoke(id);
    }

    public void Rename(string id, string name)
    {
        var d = Find(id) ?? throw new InvalidOperationException($"no device '{id}'");
        d.Name = CleanName(name);
        Save();
    }

    // ---- push ----

    public void SetPush(string id, JsonObject? subscription)
    {
        var d = Find(id) ?? throw new InvalidOperationException($"no device '{id}'");
        d.Push = subscription;
        Save();
    }

    public (string Public, string Private) VapidKeys(Func<(string, string)> generate)
    {
        if (_store.VapidPublic is { } pub && _store.VapidPrivate is { } priv) return (pub, priv);
        var (p, k) = generate();
        _store.VapidPublic = p;
        _store.VapidPrivate = k;
        Save();
        return (p, k);
    }

    // ---- LAN certificate ----

    public (byte[] Pfx, string Fingerprint) Certificate(Func<(byte[], string)> create)
    {
        if (_store.CertPfx is { } pfx && _store.CertFingerprint is { } fp) return (Convert.FromBase64String(pfx), fp);
        var (bytes, fingerprint) = create();
        _store.CertPfx = Convert.ToBase64String(bytes);
        _store.CertFingerprint = fingerprint;
        Save();
        return (bytes, fingerprint);
    }

    public string? CertificateFingerprint => _store.CertFingerprint;

    // ---- storage ----

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>"abcd efgh", "ABCD-EFGH" → "ABCDEFGH".</summary>
    internal static string Normalize(string? code) =>
        new string((code ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string CleanName(string? name)
    {
        var n = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (n.Length == 0) n = "Phone";
        return n.Length > 40 ? n[..40] : n;
    }

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            _store = JsonSerializer.Deserialize<Store>(plain, Json) ?? new Store();
        }
        catch (Exception ex)
        {
            Log.Error("remote.dat unreadable; starting with no paired devices", ex);
            _store = new Store();
        }
    }

    private void Save()
    {
        Changed?.Invoke();
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_store, Json), null, DataProtectionScope.CurrentUser);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Error("saving remote.dat", ex); }
    }
}

/// <summary>A pairing or auth failure with the HTTP status the sidecar should answer with.</summary>
public sealed class RemoteAuthException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}
