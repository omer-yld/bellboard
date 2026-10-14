using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bellboard.Remote.Push;
using Xunit;

namespace Bellboard.Tests;

public class WebPushTests
{
    // RFC 8291, section 5 and appendix A.
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Expected =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
        "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
        "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private static ECDiffieHellman Key(string pub, string priv) => ECDiffieHellman.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = WebPushCrypto.Point(Base64Url.Decode(pub)),
        D = Base64Url.Decode(priv),
    });

    [Fact]
    public void Encrypt_MatchesRfc8291Vector()
    {
        using var server = Key(AsPublic, AsPrivate);
        var body = WebPushCrypto.Encrypt(Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"),
            Base64Url.Decode(UaPublic), Base64Url.Decode(AuthSecret), server, Base64Url.Decode(Salt));
        Assert.Equal(144, body.Length); // the example's "Content-Length: 145" is off by one; its body is 144 bytes
        Assert.Equal(Expected, Base64Url.Encode(body));
    }

    [Fact]
    public void Decrypt_Rfc8291Vector_WithReceiverKey()
    {
        using var ua = Key(UaPublic, UaPrivate);
        var plain = Decrypt(Base64Url.Decode(Expected), ua, Base64Url.Decode(AuthSecret));
        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.ASCII.GetString(plain));
    }

    [Fact]
    public void Encrypt_RoundTrips_WithFreshKeys()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPushCrypto.PointBytes(ua.ExportParameters(false).Q);
        var auth = RandomNumberGenerator.GetBytes(16);
        var msg = "{\"title\":\"Claude Code · api\",\"body\":\"Needs your input\",\"pane\":\"p3\"}";

        var body1 = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(msg), uaPublic, auth);
        var body2 = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(msg), uaPublic, auth);
        Assert.NotEqual(Base64Url.Encode(body1), Base64Url.Encode(body2)); // fresh salt and server key each time

        Assert.Equal(msg, Encoding.UTF8.GetString(Decrypt(body1, ua, auth)));
        Assert.Equal(msg, Encoding.UTF8.GetString(Decrypt(body2, ua, auth)));
    }

    [Fact]
    public void Encrypt_RejectsPayloadsThatDoNotFit()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPushCrypto.PointBytes(ua.ExportParameters(false).Q);
        var auth = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(4096, WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext], uaPublic, auth).Length);
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext + 1], uaPublic, auth));
    }

    [Fact]
    public void Vapid_HeaderIsASignedJwt()
    {
        var keys = VapidKeys.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var value = WebPushCrypto.VapidAuthorization("https://fcm.googleapis.com/fcm/send/abc:def", keys, "mailto:bellboard@localhost", now);

        Assert.StartsWith("vapid t=", value);
        Assert.EndsWith(", k=" + keys.PublicKey, value);
        var jwt = value["vapid t=".Length..value.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[0])))!;
        Assert.Equal("JWT", (string?)header["typ"]);
        Assert.Equal("ES256", (string?)header["alg"]);
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[1])))!;
        Assert.Equal("https://fcm.googleapis.com", (string?)claims["aud"]);
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), (long)claims["exp"]!);
        Assert.Equal("mailto:bellboard@localhost", (string?)claims["sub"]);

        var sig = Base64Url.Decode(parts[2]);
        Assert.Equal(64, sig.Length);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = WebPushCrypto.Point(Base64Url.Decode(keys.PublicKey)),
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void VapidKeys_Generate_HasTheRightSizes()
    {
        var k = VapidKeys.Generate();
        var pub = Base64Url.Decode(k.PublicKey);
        Assert.Equal(65, pub.Length);
        Assert.Equal(0x04, pub[0]);
        Assert.Equal(32, Base64Url.Decode(k.PrivateKey).Length);
        Assert.DoesNotContain('=', k.PublicKey);
        Assert.NotEqual(k.PublicKey, VapidKeys.Generate().PublicKey);
    }

    [Fact]
    public void Subscription_FromJson()
    {
        var json = JsonNode.Parse($$$"""{"endpoint":"https://push.example.net/push/abc","expirationTime":null,"keys":{"p256dh":"{{{UaPublic}}}","auth":"{{{AuthSecret}}}"}}""");
        var sub = PushSubscription.FromJson(json);
        Assert.NotNull(sub);
        Assert.Equal("https://push.example.net/push/abc", sub!.Endpoint);
        Assert.Equal(UaPublic, sub.P256dh);
        Assert.Equal(AuthSecret, sub.Auth);
        Assert.Equal(sub, PushSubscription.FromJson(sub.ToJson()));

        Assert.Null(PushSubscription.FromJson(null));
        Assert.Null(PushSubscription.FromJson(JsonNode.Parse("""{"endpoint":"https://x.test/1"}""")));
        Assert.Null(PushSubscription.FromJson(JsonNode.Parse($$$"""{"endpoint":"http://x.test/1","keys":{"p256dh":"{{{UaPublic}}}","auth":"{{{AuthSecret}}}"}}""")));
        Assert.Null(PushSubscription.FromJson(JsonNode.Parse($$$"""{"endpoint":"https://x.test/1","keys":{"p256dh":"AAAA","auth":"{{{AuthSecret}}}"}}""")));
    }

    [Fact]
    public void Topic_IsMadeSafe()
    {
        Assert.Null(WebPushSender.SafeTopic(null));
        Assert.Equal("p3", WebPushSender.SafeTopic("p3"));
        var hashed = WebPushSender.SafeTopic("pane p3 / workspace w2 with a very long name");
        Assert.Matches("^[A-Za-z0-9_-]{32}$", hashed!);
    }

    /// <summary>The receiving side of RFC 8291, as a browser does it.</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman ua, byte[] auth)
    {
        var salt = body[..16];
        int rs = (body[16] << 24) | (body[17] << 16) | (body[18] << 8) | body[19];
        Assert.Equal(4096, rs);
        int idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var rest = body[(21 + idLen)..];

        var uaPublic = WebPushCrypto.PointBytes(ua.ExportParameters(false).Q);
        using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = WebPushCrypto.Point(asPublic) });
        var ecdh = ua.DeriveRawSecretAgreement(server.PublicKey);
        var keyInfo = new byte[14 + 65 + 65];
        Encoding.ASCII.GetBytes("WebPush: info\0").CopyTo(keyInfo, 0);
        uaPublic.CopyTo(keyInfo, 14);
        asPublic.CopyTo(keyInfo, 79);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, auth, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var cipher = rest[..^16];
        var tag = rest[^16..];
        var padded = new byte[cipher.Length];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, cipher, tag, padded);
        int end = padded.Length - 1;
        while (end >= 0 && padded[end] == 0) end--;
        Assert.Equal(0x02, padded[end]);
        return padded[..end];
    }
}
