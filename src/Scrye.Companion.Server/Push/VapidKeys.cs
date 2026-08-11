using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrye.Companion.Server.Push;

/// <summary>
/// The application server's identity for Web Push (RFC 8292, "VAPID").
///
/// <para>The push service will not accept a message without one: it wants to know who is
/// sending, so it can rate-limit and revoke. <b>This is Scrye itself</b> — which is the
/// whole point of §7.2. There is no relay to run, because the desktop signs its own
/// requests and POSTs them straight to Apple's or Google's endpoint.</para>
///
/// <para>The keypair must persist. A subscription the phone hands over is bound to the
/// public key it was created with, so regenerating on every start would silently invalidate
/// every existing subscription — notifications would simply stop, with a 403 the user never
/// sees.</para>
/// </summary>
public sealed class VapidKeys
{
    private readonly ECDsa _key;

    private VapidKeys(ECDsa key, string subject)
    {
        _key = key;
        Subject = subject;
        PublicKeyBase64Url = WebPushCrypto.ToBase64Url(ExportRawPublic(key));
    }

    /// <summary>Contact for the push service if something goes wrong — a <c>mailto:</c> or
    /// <c>https:</c> URI. Required by RFC 8292.</summary>
    public string Subject { get; }

    /// <summary>The raw public key, base64url. The client passes exactly this string to
    /// <c>pushManager.subscribe</c> as <c>applicationServerKey</c>.</summary>
    public string PublicKeyBase64Url { get; }

    /// <summary>Load the keypair from <paramref name="path"/>, generating and saving one on
    /// first use. Any unreadable or corrupt file is replaced rather than throwing: a broken
    /// key file should cost you your subscriptions, not the ability to start Scrye.</summary>
    // The subject must be a contact URI on a REAL domain. Apple validates the domain and
    // rejects the whole JWT as BadJwtToken over an "@localhost" address — a 403 that FCM
    // never gives, so it only surfaces the first time an iPhone registers. The project
    // page is a valid https: contact per RFC 8292 and can never go stale the way an
    // email default would.
    public static VapidKeys LoadOrCreate(string path, string subject = "https://github.com/Eldran/Scrye")
    {
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("d", out JsonElement d) &&
                    doc.RootElement.TryGetProperty("q", out JsonElement q))
                {
                    byte[] raw = WebPushCrypto.FromBase64Url(q.GetString() ?? "");
                    var ec = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        D = WebPushCrypto.FromBase64Url(d.GetString() ?? ""),
                        Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
                    });
                    return new VapidKeys(ec, subject);
                }
            }
        }
        catch (Exception) { /* fall through and mint a new one */ }

        ECDsa fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            ECParameters p = fresh.ExportParameters(true);
            string json = JsonSerializer.Serialize(new
            {
                d = WebPushCrypto.ToBase64Url(p.D!),
                q = WebPushCrypto.ToBase64Url(ExportRawPublic(fresh)),
            });

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
        catch (Exception) { /* unwritable disk: keep the in-memory key for this run */ }

        return new VapidKeys(fresh, subject);
    }

    /// <summary>
    /// The <c>Authorization</c> header value for a push request to
    /// <paramref name="endpoint"/>.
    ///
    /// <para>The JWT's audience is the endpoint's <b>origin</b>, not the full URL, and it is
    /// signed per-origin — so a token minted for Apple's push service is not valid at
    /// Google's. Expiry is capped well under RFC 8292's 24-hour maximum; tokens are cheap to
    /// mint, so there is no reason to sail close to it.</para>
    /// </summary>
    public string CreateAuthorizationHeader(Uri endpoint, DateTimeOffset now)
    {
        string audience = endpoint.GetLeftPart(UriPartial.Authority);
        long exp = now.AddHours(12).ToUnixTimeSeconds();

        string header = WebPushCrypto.ToBase64Url(
            Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        string payload = WebPushCrypto.ToBase64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { aud = audience, exp, sub = Subject })));

        byte[] signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");

        // ES256 wants the raw r||s concatenation, NOT the DER encoding ECDsa produces by
        // default. Getting this wrong yields a token every push service rejects as malformed.
        byte[] signature = _key.SignData(
            signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        string jwt = $"{header}.{payload}.{WebPushCrypto.ToBase64Url(signature)}";
        return $"vapid t={jwt}, k={PublicKeyBase64Url}";
    }

    /// <summary>Verify a header this instance produced. Exists so the tests can assert the
    /// signature really validates rather than merely being the right length.</summary>
    public bool VerifyForTest(string authorizationHeader)
    {
        try
        {
            string jwt = authorizationHeader.Split("t=")[1].Split(',')[0].Trim();
            string[] parts = jwt.Split('.');
            if (parts.Length != 3) return false;
            return _key.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                WebPushCrypto.FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception) { return false; }
    }

    private static byte[] ExportRawPublic(ECDsa key)
    {
        ECParameters p = key.ExportParameters(false);
        byte[] raw = new byte[65];
        raw[0] = 0x04;
        p.Q.X!.CopyTo(raw, 1);
        p.Q.Y!.CopyTo(raw, 33);
        return raw;
    }
}
