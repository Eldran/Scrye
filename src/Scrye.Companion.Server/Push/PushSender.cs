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

    /// <summary>Why the most recent <see cref="SendAsync"/> was rejected — HTTP status,
    /// endpoint host, and the push service's own reason string (Apple and Google both
    /// explain failures in the response body). Null after a success. Exists because a
    /// silent Failed is indistinguishable from working, which is how a broken VAPID key
    /// or clock skew hides for months.</summary>
    public string? LastError { get; private set; }

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
            // Re-checked here as well as at subscribe time: subscriptions also come back from
            // disk, and this POST carries our VAPID Authorization header.
            if (!IsAllowedEndpoint(endpoint))
            {
                LastError = $"refusing push endpoint host {endpoint.Host}: not a known push service";
                return PushResult.Failed;
            }

            byte[] plaintext = Encoding.UTF8.GetBytes(payload);
            // Cutting the bytes would leave invalid JSON (and possibly half a UTF-8 sequence)
            // that the service worker cannot parse. BuildPayload already trims the body to
            // fit, so an oversized payload here is a caller bug: fail loudly instead.
            if (plaintext.Length > MaxPayloadBytes)
            {
                LastError = $"payload is {plaintext.Length} bytes, over the {MaxPayloadBytes}-byte limit";
                return PushResult.Failed;
            }

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

            if (res.IsSuccessStatusCode) { LastError = null; return PushResult.Delivered; }
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return PushResult.Expired;
            string reason = "";
            try
            {
                reason = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                if (reason.Length > 160) reason = reason[..160];
            }
            catch (Exception) { /* the status alone still helps */ }
            LastError = $"{(int)res.StatusCode} from {endpoint.Host}" + (reason.Length > 0 ? $": {reason}" : "");
            return PushResult.Failed;
        }
        catch (OperationCanceledException) { LastError = "timed out reaching the push service"; return PushResult.Failed; }
        catch (Exception ex) { LastError = ex.Message; return PushResult.Failed; }
    }

    /// <summary>The JSON a service worker receives. Kept deliberately small — the payload is
    /// size-limited and everything here crosses an encrypted channel to a locked phone.</summary>
    public static string BuildPayload(string title, string body, string? sessionId = null)
    {
        // Trim the BODY until the serialised JSON fits, rather than cutting the finished
        // bytes (which breaks the JSON). Escaping can inflate text, so measure the real
        // output and shrink by the overshoot; each pass strictly shortens the body.
        body ??= "";
        string json = JsonSerializer.Serialize(new { title, body, sessionId });
        while (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes && body.Length > 0)
        {
            int over = Encoding.UTF8.GetByteCount(json) - MaxPayloadBytes;
            int keep = Math.Max(0, body.Length - Math.Max(over, 16) - 1);
            if (keep > 0 && char.IsHighSurrogate(body[keep - 1])) keep--;   // never split a pair
            body = keep > 0 ? body[..keep] + "\u2026" : "";
            json = JsonSerializer.Serialize(new { title, body, sessionId });
        }
        return json;
    }

    /// <summary>Hosts of the browser vendors' push services. A subscription endpoint is
    /// client-supplied, and we POST to it with our VAPID Authorization header, so an open
    /// endpoint would let any device make the desktop send requests to LAN or loopback
    /// services (SSRF). Entries starting with '.' match any subdomain.</summary>
    private static readonly string[] PushHosts =
    {
        "fcm.googleapis.com",                 // Chrome, Edge on Android, Opera, Samsung
        ".push.apple.com",                    // Safari / iOS (web.push.apple.com)
        "updates.push.services.mozilla.com",  // Firefox
        ".push.services.mozilla.com",
        ".notify.windows.com",                // Edge on Windows (WNS)
    };

    /// <summary>Whether <paramref name="endpoint"/> is an https URL on a known push service's
    /// host name, on the default port. IP literals and localhost are never allowed.</summary>
    public static bool IsAllowedEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && IsAllowedEndpoint(uri);

    public static bool IsAllowedEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort) return false;
        if (endpoint.HostNameType != UriHostNameType.Dns || endpoint.IsLoopback) return false;
        if (endpoint.UserInfo.Length > 0) return false;

        string host = endpoint.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (string allowed in PushHosts)
        {
            if (allowed[0] == '.' ? host.EndsWith(allowed, StringComparison.Ordinal)
                                  : host == allowed)
                return true;
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
