using System.Security.Cryptography;
using System.Text;
using Scrye.Companion.Server.Push;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Web Push message encryption (RFC 8291 over RFC 8188's aes128gcm).
///
/// <para>These tests matter more than most. Every failure mode here is <b>silent</b>: a
/// wrong key order, a wrong padding delimiter or a mis-framed header produces a
/// well-formed-looking payload that the phone simply discards, with no error anywhere on
/// the desktop. The only way to know it is right is to reproduce the RFC's own worked
/// example byte for byte, which is what the first test does.</para>
/// </summary>
public class WebPushCryptoTests
{
    // RFC 8291 §5, verbatim.
    private const string RfcPlaintext = "When I grow up, I want to be a watermelon";
    private const string RfcAuth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string RfcUaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string RfcUaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string RfcAsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string RfcAsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string RfcSalt = "DGv6ra1nlYgDCS1FRnbzlw";

    private const string RfcExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
        "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
        "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private static byte[] B64(string s) => WebPushCrypto.FromBase64Url(s);

    [Fact]
    public void ReproducesTheRfc8291WorkedExample()
    {
        using ECDiffieHellman sender = WebPushCrypto.ImportRawPrivateKey(B64(RfcAsPrivate), B64(RfcAsPublic));

        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(RfcPlaintext),
            B64(RfcUaPublic),
            B64(RfcAuth),
            sender,
            B64(RfcSalt));

        Assert.Equal(RfcExpectedBody, WebPushCrypto.ToBase64Url(body));
    }

    [Fact]
    public void ReceiverCanDecryptTheRfcExample()
    {
        using ECDiffieHellman sender = WebPushCrypto.ImportRawPrivateKey(B64(RfcAsPrivate), B64(RfcAsPublic));
        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(RfcPlaintext), B64(RfcUaPublic), B64(RfcAuth), sender, B64(RfcSalt));

        Assert.Equal(RfcPlaintext, Decrypt(body, B64(RfcUaPrivate), B64(RfcUaPublic), B64(RfcAuth)));
    }

    [Fact]
    public void FreshKeysAndSaltStillRoundTrip()
    {
        // Production path: no fixed sender key, no fixed salt.
        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes("Erik tells you: on my way"), B64(RfcUaPublic), B64(RfcAuth));

        Assert.Equal("Erik tells you: on my way",
            Decrypt(body, B64(RfcUaPrivate), B64(RfcUaPublic), B64(RfcAuth)));
    }

    [Fact]
    public void EachEncryptionIsUnique()
    {
        // A repeated salt/key would leak that two notifications carried the same text.
        byte[] plaintext = Encoding.UTF8.GetBytes("same text");
        byte[] a = WebPushCrypto.Encrypt(plaintext, B64(RfcUaPublic), B64(RfcAuth));
        byte[] b = WebPushCrypto.Encrypt(plaintext, B64(RfcUaPublic), B64(RfcAuth));

        Assert.NotEqual(WebPushCrypto.ToBase64Url(a), WebPushCrypto.ToBase64Url(b));
    }

    [Fact]
    public void BodyIsFramedPerRfc8188()
    {
        using ECDiffieHellman sender = WebPushCrypto.ImportRawPrivateKey(B64(RfcAsPrivate), B64(RfcAsPublic));
        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(RfcPlaintext), B64(RfcUaPublic), B64(RfcAuth), sender, B64(RfcSalt));

        Assert.Equal(B64(RfcSalt), body[..16]);                       // salt
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        Assert.Equal(65, body[20]);                                    // key id length
        Assert.Equal(B64(RfcAsPublic), body[21..86]);                  // the sender's public key
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(66)]
    public void RejectsMalformedSubscriptionKey(int length) =>
        Assert.Throws<ArgumentException>(() =>
            WebPushCrypto.Encrypt(new byte[] { 1 }, new byte[length], B64(RfcAuth)));

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void RejectsMalformedAuthSecret(int length) =>
        Assert.Throws<ArgumentException>(() =>
            WebPushCrypto.Encrypt(new byte[] { 1 }, B64(RfcUaPublic), new byte[length]));

    [Fact]
    public void Base64UrlRoundTripsIncludingPaddingEdges()
    {
        for (int n = 1; n <= 8; n++)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(n);
            string encoded = WebPushCrypto.ToBase64Url(bytes);

            Assert.False(encoded.Contains('='), "base64url is unpadded");
            Assert.False(encoded.Contains('+'), "base64url uses '-' not '+'");
            Assert.False(encoded.Contains('/'), "base64url uses '_' not '/'");
            Assert.Equal(bytes, WebPushCrypto.FromBase64Url(encoded));
        }
    }

    [Fact]
    public void ExportedPublicKeyIsAnUncompressedPoint()
    {
        using ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] raw = WebPushCrypto.ExportRawPublicKey(key);

        Assert.Equal(65, raw.Length);
        Assert.Equal(0x04, raw[0]);
    }

    // ---- receiver side, for verification only -------------------------------

    /// <summary>The user agent's half of RFC 8291, so the tests can assert on what the phone
    /// would actually read rather than only on byte equality.</summary>
    private static string Decrypt(byte[] body, byte[] uaPrivate, byte[] uaPublic, byte[] auth)
    {
        byte[] salt = body[..16];
        int idlen = body[20];
        byte[] asPublic = body[21..(21 + idlen)];
        byte[] payload = body[(21 + idlen)..];

        using ECDiffieHellman ua = WebPushCrypto.ImportRawPrivateKey(uaPrivate, uaPublic);
        using ECDiffieHellman asPub = WebPushCrypto.ImportRawPublicKey(asPublic);
        byte[] ecdh = ua.DeriveRawSecretAgreement(asPub.PublicKey);

        var keyInfo = new List<byte>();
        keyInfo.AddRange(Encoding.ASCII.GetBytes("WebPush: info"));
        keyInfo.Add(0);
        keyInfo.AddRange(uaPublic);
        keyInfo.AddRange(asPublic);

        byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdh, auth);
        byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo.ToArray());
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        byte[] ct = payload[..^16];
        byte[] tag = payload[^16..];
        byte[] plain = new byte[ct.Length];
        using (var gcm = new AesGcm(cek, 16))
            gcm.Decrypt(nonce, ct, tag, plain);

        int end = plain.Length;
        while (end > 0 && plain[end - 1] == 0) end--;
        if (end > 0 && plain[end - 1] == 0x02) end--;   // last-record delimiter
        return Encoding.UTF8.GetString(plain, 0, end);
    }
}
