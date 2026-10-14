using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bellboard.Remote.Push;

/// <summary>A VAPID key pair (RFC 8292), base64url: the 65-byte uncompressed P-256 point and the 32-byte private scalar.</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey)
{
    public static VapidKeys Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(includePrivateParameters: true);
        return new VapidKeys(Base64Url.Encode(WebPushCrypto.PointBytes(p.Q)), Base64Url.Encode(p.D!));
    }

    internal ECDsa ToECDsa()
    {
        var q = WebPushCrypto.Point(Base64Url.Decode(PublicKey));
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q, D = Base64Url.Decode(PrivateKey) });
    }
}

/// <summary>Where and how to reach one browser: the shape of the browser's <c>PushSubscription.toJSON()</c>.</summary>
public sealed record PushSubscription(string Endpoint, string P256dh, string Auth)
{
    /// <summary>Reads <c>{endpoint, keys:{p256dh, auth}}</c>; null when anything is missing or not https.</summary>
    public static PushSubscription? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var endpoint = Text(o["endpoint"]);
        var keys = o["keys"] as JsonObject;
        var p256dh = Text(keys?["p256dh"]);
        var auth = Text(keys?["auth"]);
        if (endpoint == null || p256dh == null || auth == null) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            if (Base64Url.Decode(p256dh).Length != 65 || Base64Url.Decode(auth).Length < 16) return null;
        }
        catch (FormatException) { return null; }
        return new PushSubscription(endpoint, p256dh, auth);
    }

    public JsonObject ToJson() => new()
    {
        ["endpoint"] = Endpoint,
        ["keys"] = new JsonObject { ["p256dh"] = P256dh, ["auth"] = Auth },
    };

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

/// <summary>Message encryption for Web Push (RFC 8291, aes128gcm content coding of RFC 8188) and VAPID (RFC 8292).</summary>
public static class WebPushCrypto
{
    public const int RecordSize = 4096;
    /// <summary>The largest plaintext that fits a 4096-byte push body: 86-byte header, 1 padding delimiter, 16-byte tag.</summary>
    public const int MaxPlaintext = RecordSize - 86 - 1 - 16;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>Encrypts a push message for the browser that owns <paramref name="uaPublic"/> (p256dh) and <paramref name="authSecret"/>.</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, uaPublic, authSecret, server, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Encrypts with a given server key pair and salt (for test vectors).</summary>
    internal static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman server, byte[] salt)
    {
        if (plaintext.Length > MaxPlaintext)
            throw new ArgumentException($"push payload is {plaintext.Length} bytes; at most {MaxPlaintext} fit", nameof(plaintext));
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04) throw new ArgumentException("p256dh must be an uncompressed P-256 point", nameof(uaPublic));
        if (salt.Length != 16) throw new ArgumentException("salt must be 16 bytes", nameof(salt));

        var asPublic = PointBytes(server.ExportParameters(false).Q);
        byte[] ecdhSecret;
        using (var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(uaPublic) }))
            ecdhSecret = server.DeriveRawSecretAgreement(ua.PublicKey);

        var keyInfo = Concat(KeyInfoPrefix, uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // A single record: the plaintext, then the 0x02 delimiter that marks the last record (no further padding).
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02;
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, padded, cipher, tag);

        // Header: salt (16) | record size (4, big endian) | key id length (1) | key id = server public key (65).
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>The <c>Authorization</c> header value for a push to <paramref name="endpoint"/>: <c>vapid t=&lt;JWT&gt;, k=&lt;public key&gt;</c>.</summary>
    public static string VapidAuthorization(string endpoint, VapidKeys keys, string subject, DateTimeOffset now)
    {
        var uri = new Uri(endpoint);
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = new JsonObject
        {
            ["aud"] = uri.GetLeftPart(UriPartial.Authority),
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        };
        var body = Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + body);
        using var ec = keys.ToECDsa();
        var sig = ec.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{body}.{Base64Url.Encode(sig)}, k={keys.PublicKey}";
    }

    internal static byte[] PointBytes(ECPoint q)
    {
        var b = new byte[65];
        b[0] = 0x04;
        q.X!.CopyTo(b, 1);
        q.Y!.CopyTo(b, 33);
        return b;
    }

    internal static ECPoint Point(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04) throw new ArgumentException("not an uncompressed P-256 point");
        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(r, at); at += p.Length; }
        return r;
    }
}

public enum PushOutcome { Ok, Gone, Failed }

/// <summary>What the push service said. <see cref="PushOutcome.Gone"/>: the subscription expired; forget it.</summary>
public sealed record PushResult(PushOutcome Outcome, int Status = 0, string? Reason = null);

/// <summary>Sends encrypted pushes straight to the browser's push service (FCM, Mozilla, Apple…). No Bellboard server.</summary>
public sealed class WebPushSender(HttpClient http, VapidKeys keys, string subject)
{
    private static readonly Regex TopicSafe = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    public async Task<PushResult> SendAsync(PushSubscription sub, string payloadJson, int ttlSeconds = 3600, string urgency = "high",
        string? topic = null, CancellationToken ct = default)
    {
        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(payloadJson), Base64Url.Decode(sub.P256dh), Base64Url.Decode(sub.Auth));
        using var req = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", Math.Max(0, ttlSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("Urgency", urgency);
        if (SafeTopic(topic) is { } t) req.Headers.TryAddWithoutValidation("Topic", t);
        req.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(sub.Endpoint, keys, subject, DateTimeOffset.UtcNow));
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return new PushResult(PushOutcome.Ok, (int)resp.StatusCode);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return new PushResult(PushOutcome.Gone, (int)resp.StatusCode);
            string reason;
            try { reason = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { reason = ""; }
            if (reason.Length > 300) reason = reason[..300];
            return new PushResult(PushOutcome.Failed, (int)resp.StatusCode, string.IsNullOrWhiteSpace(reason) ? resp.ReasonPhrase : reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new PushResult(PushOutcome.Failed, 0, ex.Message);
        }
    }

    /// <summary>A Topic header must be ≤ 32 base64url characters; anything else is replaced by a hash of it.</summary>
    internal static string? SafeTopic(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;
        if (TopicSafe.IsMatch(topic)) return topic;
        return Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(topic)))[..32];
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        s = s.TrimEnd('=');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }
}
