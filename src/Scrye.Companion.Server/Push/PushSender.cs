using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrye.Companion.Server.Push;

/// <summary>One device's push subscription, exactly as <c>pushManager.subscribe</c> returns it.</summary>
public sealed record PushSubscription(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("p256dh")] string P256dh,
    [property: JsonPropertyName("auth")] string Auth)
{
    /// <summary>Stable identity for storage and de-duplication. The endpoint URL already
    /// uniquely identifies a subscription, and a device that re-subscribes gets a new one.</summary>
    public string Id => Endpoint;
}

/// <summary>What happened when we tried to deliver.</summary>
public enum PushResult
{
    Delivered,

    /// <summary>The push service says this subscription is dead (404/410). The caller must
    /// forget it — retrying forever is how you end up rate-limited.</summary>
    Expired,

    /// <summary>Rejected for some other reason: bad VAPID token, payload too large, rate
    /// limit. Worth logging, not worth discarding the subscription over.</summary>
    Failed,
}

/// <summary>
/// Sends encrypted Web Push messages straight from the desktop to the browser vendor's push
/// service.
///
/// <para>This is the piece that makes §7.2 true: there is no relay, no account, and no
/// service to operate. The desktop needs nothing but outbound HTTPS, which it already has
/// for the MUD connection.</para>
/// </summary>
public sealed class PushSender : IDisposable
{
    private readonly HttpClient _http;
    private readonly VapidKeys _vapid;

    /// <summary>Apple rejects oversized payloads outright. Notification text is short by
    /// nature, so truncating is better than a delivery that silently fails.</summary>
    public const int MaxPayloadBytes = 3000;

    public PushSender(VapidKeys vapid, HttpClient? http = null)
    {
        _vapid = vapid;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<PushResult> SendAsync(
        PushSubscription sub,
        string payload,
        DateTimeOffset now,
        int ttlSeconds = 3600,
        CancellationToken ct = default)
    {
        try
        {
            if (!Uri.TryCreate(sub.Endpoint, UriKind.Absolute, out Uri? endpoint)) return PushResult.Failed;

            byte[] plaintext = Encoding.UTF8.GetBytes(payload);
            if (plaintext.Length > MaxPayloadBytes) plaintext = plaintext[..MaxPayloadBytes];

            byte[] body = WebPushCrypto.Encrypt(
                plaintext,
                WebPushCrypto.FromBase64Url(sub.P256dh),
                WebPushCrypto.FromBase64Url(sub.Auth));

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content.Headers.ContentEncoding.Add("aes128gcm");
            // TTL is mandatory. Zero would mean "deliver now or discard", which loses the
            // notification whenever the phone is briefly offline — the exact case this exists for.
            req.Headers.TryAddWithoutValidation("TTL", ttlSeconds.ToString());
            req.Headers.TryAddWithoutValidation("Urgency", "normal");
            req.Headers.TryAddWithoutValidation(
                "Authorization", _vapid.CreateAuthorizationHeader(endpoint, now));

            using HttpResponseMessage res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.IsSuccessStatusCode) return PushResult.Delivered;
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return PushResult.Expired;
            return PushResult.Failed;
        }
        catch (OperationCanceledException) { return PushResult.Failed; }
        catch (Exception) { return PushResult.Failed; }
    }

    /// <summary>The JSON a service worker receives. Kept deliberately small — the payload is
    /// size-limited and everything here crosses an encrypted channel to a locked phone.</summary>
    public static string BuildPayload(string title, string body, string? sessionId = null) =>
        JsonSerializer.Serialize(new { title, body, sessionId });

    public void Dispose() => _http.Dispose();
}
