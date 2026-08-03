using System.Security.Cryptography;
using System.Text;

namespace Scrye.Companion.Server.Push;

/// <summary>
/// Message encryption for Web Push — RFC 8291 over the <c>aes128gcm</c> content coding of
/// RFC 8188.
///
/// <para>This is why §7.2 can promise notifications with no hosted component: the payload is
/// encrypted here, with keys the push service never sees, so Apple and Google forward
/// ciphertext and there is nothing for anyone else to operate or be trusted with. Written
/// by hand rather than pulled from a library because <c>Scrye.Core</c> and its companion
/// projects take no NuGet dependencies — and .NET has every primitive in-box.</para>
///
/// <para>Correctness here is not eyeballable, so <c>Encrypt</c> takes the ephemeral keypair
/// and salt as optional parameters purely so the tests can reproduce RFC 8291 §5's worked
/// example byte for byte. Production callers omit them and get fresh randomness.</para>
/// </summary>
public static class WebPushCrypto
{
    private const int KeyLength = 65;          // uncompressed P-256 point: 0x04 || X(32) || Y(32)
    private const int SaltLength = 16;
    private const int RecordSize = 4096;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info");
    private static readonly byte[] CekInfo = Concat(Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm"), new byte[] { 0 });
    private static readonly byte[] NonceInfo = Concat(Encoding.ASCII.GetBytes("Content-Encoding: nonce"), new byte[] { 0 });

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> for a subscription.
    /// </summary>
    /// <param name="uaPublicKey">The subscription's <c>p256dh</c>, raw 65-byte point.</param>
    /// <param name="authSecret">The subscription's <c>auth</c>, 16 bytes.</param>
    /// <param name="senderPrivate">Test-only: a fixed ephemeral key. Null generates one.</param>
    /// <param name="salt">Test-only: a fixed salt. Null generates one.</param>
    /// <returns>The complete request body, ready to POST.</returns>
    public static byte[] Encrypt(
        byte[] plaintext,
        byte[] uaPublicKey,
        byte[] authSecret,
        ECDiffieHellman? senderPrivate = null,
        byte[]? salt = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (uaPublicKey is not { Length: KeyLength })
            throw new ArgumentException($"p256dh must be {KeyLength} raw bytes", nameof(uaPublicKey));
        if (authSecret is not { Length: 16 })
            throw new ArgumentException("auth secret must be 16 bytes", nameof(authSecret));

        bool ownsSender = senderPrivate is null;
        ECDiffieHellman sender = senderPrivate ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            salt ??= RandomNumberGenerator.GetBytes(SaltLength);
            byte[] asPublic = ExportRawPublicKey(sender);

            using ECDiffieHellman ua = ImportRawPublicKey(uaPublicKey);
            byte[] ecdhSecret = sender.DeriveRawSecretAgreement(ua.PublicKey);

            // RFC 8291 §3.4. Note the ORDER inside key_info: user agent key first, then
            // application server key. Swapping them produces a plausible-looking payload
            // that the phone silently fails to decrypt.
            byte[] keyInfo = Concat(KeyInfoPrefix, new byte[] { 0 }, uaPublicKey, asPublic);

            byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret);
            byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

            byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
            byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
            byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

            // A single record, so the padding delimiter is 0x02 ("last record"). 0x01 here
            // would mean "more records follow" and the receiver would wait for one.
            byte[] padded = Concat(plaintext, new byte[] { 0x02 });

            byte[] ciphertext = new byte[padded.Length];
            byte[] tag = new byte[16];
            using (var gcm = new AesGcm(cek, tag.Length))
                gcm.Encrypt(nonce, padded, ciphertext, tag);

            // RFC 8188 §2 header: salt(16) || rs(4, big-endian) || idlen(1) || keyid
            byte[] header = new byte[SaltLength + 4 + 1 + KeyLength];
            salt.CopyTo(header, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                header.AsSpan(SaltLength, 4), RecordSize);
            header[SaltLength + 4] = KeyLength;
            asPublic.CopyTo(header, SaltLength + 5);

            return Concat(header, ciphertext, tag);
        }
        finally
        {
            if (ownsSender) sender.Dispose();
        }
    }

    /// <summary>Raw uncompressed point for an ECDH key, the form Web Push uses everywhere.</summary>
    public static byte[] ExportRawPublicKey(ECDiffieHellman key)
    {
        ECParameters p = key.ExportParameters(false);
        return Concat(new byte[] { 0x04 }, p.Q.X!, p.Q.Y!);
    }

    public static ECDiffieHellman ImportRawPublicKey(byte[] raw)
    {
        if (raw is not { Length: KeyLength } || raw[0] != 0x04)
            throw new ArgumentException("expected a 65-byte uncompressed P-256 point", nameof(raw));

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
        });
    }

    /// <summary>Rebuild an ECDH keypair from a raw private scalar plus its public point.
    /// Only used to replay the RFC's test vector; real ephemeral keys are generated.</summary>
    public static ECDiffieHellman ImportRawPrivateKey(byte[] d, byte[] rawPublic)
    {
        if (rawPublic is not { Length: KeyLength } || rawPublic[0] != 0x04)
            throw new ArgumentException("expected a 65-byte uncompressed P-256 point", nameof(rawPublic));

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = d,
            Q = new ECPoint { X = rawPublic[1..33], Y = rawPublic[33..65] },
        });
    }

    // ---- base64url ----------------------------------------------------------
    //
    // Web Push uses unpadded base64url throughout — subscription keys, VAPID keys, JWTs.

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        string t = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        byte[] result = new byte[total];
        int at = 0;
        foreach (byte[] p in parts) { p.CopyTo(result, at); at += p.Length; }
        return result;
    }
}
