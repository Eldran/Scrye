using System.Net;
using System.Text;
using System.Text.Json;
using Scrye.Companion.Server.Push;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// VAPID identity, subscription storage, and delivery — the parts around the encryption
/// that decide whether a notification ever reaches a phone.
///
/// <para>Two properties here are worth more than the rest: the VAPID keypair and the
/// subscription list must <b>survive a restart</b>. Losing either breaks notifications
/// permanently and invisibly — the desktop keeps sending, the push service keeps refusing,
/// and nothing surfaces.</para>
/// </summary>
public class PushDeliveryTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string TempFile(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    // ---- VAPID ---------------------------------------------------------------

    [Fact]
    public void VapidKeyPairPersistsAcrossLoads()
    {
        string path = TempFile("vapid");

        VapidKeys first = VapidKeys.LoadOrCreate(path);
        VapidKeys second = VapidKeys.LoadOrCreate(path);

        // A regenerated key silently invalidates every existing subscription.
        Assert.Equal(first.PublicKeyBase64Url, second.PublicKeyBase64Url);
    }

    [Fact]
    public void VapidPublicKeyIsAP256Point()
    {
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        byte[] raw = WebPushCrypto.FromBase64Url(keys.PublicKeyBase64Url);

        Assert.Equal(65, raw.Length);
        Assert.Equal(0x04, raw[0]);
    }

    [Fact]
    public void CorruptKeyFileRegeneratesRatherThanThrowing()
    {
        string path = TempFile("vapid");
        File.WriteAllText(path, "{ this is not json");

        VapidKeys keys = VapidKeys.LoadOrCreate(path);

        Assert.NotEmpty(keys.PublicKeyBase64Url);
    }

    [Fact]
    public void AuthorizationHeaderIsAVerifiableEs256Token()
    {
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        var endpoint = new Uri("https://web.push.apple.com/some/device/path");

        string header = keys.CreateAuthorizationHeader(endpoint, DateTimeOffset.UtcNow);

        Assert.StartsWith("vapid t=", header);
        Assert.Contains("k=" + keys.PublicKeyBase64Url, header);
        Assert.True(keys.VerifyForTest(header));
    }

    [Fact]
    public void JwtAudienceIsTheOriginNotTheFullEndpoint()
    {
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        var endpoint = new Uri("https://web.push.apple.com/some/device/path?x=1");

        string payload = DecodeJwtPart(keys.CreateAuthorizationHeader(endpoint, DateTimeOffset.UtcNow), 1);
        using JsonDocument doc = JsonDocument.Parse(payload);

        Assert.Equal("https://web.push.apple.com", doc.RootElement.GetProperty("aud").GetString());
        Assert.True(doc.RootElement.TryGetProperty("exp", out _));
        Assert.True(doc.RootElement.TryGetProperty("sub", out _));
    }

    [Fact]
    public void SubjectIsAContactUriOnARealDomain()
    {
        // Apple validates the sub claim's domain and rejects "@localhost" with BadJwtToken —
        // silently killing iOS push while FCM keeps working. Pin the default to something
        // with a routable domain so the regression can't sneak back in.
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));

        Assert.True(keys.Subject.StartsWith("mailto:") || keys.Subject.StartsWith("https://"),
            "RFC 8292: sub must be a mailto: or https: URI");
        Assert.DoesNotContain("localhost", keys.Subject);
        string host = keys.Subject.StartsWith("mailto:")
            ? keys.Subject.Split('@')[^1]
            : new Uri(keys.Subject).Host;
        Assert.Contains(".", host);   // a real, dotted domain — what Apple actually checks
    }

    [Fact]
    public void JwtSignatureIsRawR_S_NotDer()
    {
        // ECDsa produces DER by default; push services reject that as malformed.
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        string header = keys.CreateAuthorizationHeader(new Uri("https://fcm.googleapis.com/x"), DateTimeOffset.UtcNow);

        string jwt = header.Split("t=")[1].Split(',')[0].Trim();
        Assert.Equal(64, WebPushCrypto.FromBase64Url(jwt.Split('.')[2]).Length);
    }

    [Fact]
    public void JwtHeaderDeclaresEs256()
    {
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        string header = keys.CreateAuthorizationHeader(new Uri("https://fcm.googleapis.com/x"), DateTimeOffset.UtcNow);

        Assert.Contains("ES256", DecodeJwtPart(header, 0));
    }

    [Fact]
    public void TokensAreMintedPerOrigin()
    {
        VapidKeys keys = VapidKeys.LoadOrCreate(TempFile("vapid"));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string apple = keys.CreateAuthorizationHeader(new Uri("https://web.push.apple.com/a"), now);
        string google = keys.CreateAuthorizationHeader(new Uri("https://fcm.googleapis.com/b"), now);

        Assert.NotEqual(apple, google);
    }

    // ---- subscription storage -----------------------------------------------

    [Fact]
    public void ResubscribingReplacesRatherThanDuplicates()
    {
        var store = new PushStore();
        store.Add(new PushSubscription("https://p/1", "k1", "a1"));
        store.Add(new PushSubscription("https://p/1", "k2", "a2"));

        Assert.Equal(1, store.Count);
        Assert.Equal("k2", store.All.Single().P256dh);
    }

    [Fact]
    public void SubscriptionsPersistAcrossRestarts()
    {
        string path = TempFile("push");

        var first = new PushStore(path);
        first.Add(new PushSubscription("https://p/1", "k1", "a1"));
        first.Add(new PushSubscription("https://p/2", "k2", "a2"));

        var second = new PushStore(path);

        Assert.Equal(2, second.Count);
        Assert.Contains(second.All, s => s.Endpoint == "https://p/2");
    }

    [Fact]
    public void CorruptSubscriptionFileStartsEmptyRatherThanThrowing()
    {
        string path = TempFile("push");
        File.WriteAllText(path, "not json at all");

        Assert.Equal(0, new PushStore(path).Count);
    }

    [Fact]
    public void RemoveTakesEffectAndPersists()
    {
        string path = TempFile("push");
        var store = new PushStore(path);
        store.Add(new PushSubscription("https://p/1", "k", "a"));

        Assert.True(store.Remove("https://p/1"));
        Assert.False(store.Remove("https://p/1"));
        Assert.Equal(0, new PushStore(path).Count);
    }

    // ---- delivery ------------------------------------------------------------

    [Fact]
    public async Task SendSetsTheHeadersPushServicesRequire()
    {
        HttpRequestMessage? seen = null;
        var sender = MakeSender(HttpStatusCode.Created, req => seen = req, out _);

        PushResult result = await sender.SendAsync(Subscription(), "{}", DateTimeOffset.UtcNow);

        Assert.Equal(PushResult.Delivered, result);
        Assert.NotNull(seen);
        Assert.StartsWith("vapid t=", seen!.Headers.GetValues("Authorization").Single());
        Assert.Equal("aes128gcm", seen.Content!.Headers.ContentEncoding.Single());
        // TTL 0 would mean "deliver now or discard", losing exactly the notifications this
        // feature exists for — the ones that arrive while the phone is asleep.
        Assert.NotEqual("0", seen.Headers.GetValues("TTL").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, PushResult.Delivered)]
    [InlineData(HttpStatusCode.Created, PushResult.Delivered)]
    [InlineData(HttpStatusCode.NotFound, PushResult.Expired)]
    [InlineData(HttpStatusCode.Gone, PushResult.Expired)]
    [InlineData(HttpStatusCode.InternalServerError, PushResult.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, PushResult.Failed)]
    public async Task StatusCodesMapToTheRightOutcome(HttpStatusCode status, PushResult expected)
    {
        PushSender sender = MakeSender(status, _ => { }, out _);

        Assert.Equal(expected, await sender.SendAsync(Subscription(), "{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExpiredSubscriptionsArePruned()
    {
        PushSender sender = MakeSender(HttpStatusCode.Gone, _ => { }, out _);
        var store = new PushStore();
        store.Add(Subscription());
        var notifier = new PushNotifier(store, sender);

        PushOutcome outcome = await notifier.NotifyAsync("t", "b", null, DateTimeOffset.UtcNow);

        Assert.Equal(0, outcome.Delivered);
        Assert.Equal(1, outcome.Expired);
        Assert.Equal(0, store.Count);   // retrying a dead endpoint forever invites a rate limit
    }

    [Fact]
    public async Task TransientFailuresDoNotPrune()
    {
        // The device is fine; the service hiccuped. Dropping it would need a re-opt-in.
        PushSender sender = MakeSender(HttpStatusCode.InternalServerError, _ => { }, out _);
        var store = new PushStore();
        store.Add(Subscription());
        var notifier = new PushNotifier(store, sender);

        await notifier.NotifyAsync("t", "b", null, DateTimeOffset.UtcNow);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task AnUnreachableEndpointFailsRatherThanThrows()
    {
        PushSender sender = MakeSender(HttpStatusCode.OK, _ => throw new HttpRequestException("no route"), out _);

        Assert.Equal(PushResult.Failed, await sender.SendAsync(Subscription(), "{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MalformedEndpointFailsCleanly()
    {
        PushSender sender = MakeSender(HttpStatusCode.OK, _ => { }, out _);
        var bad = new PushSubscription("not a url", RfcUaPublicKey, RfcAuthSecret);

        Assert.Equal(PushResult.Failed, await sender.SendAsync(bad, "{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PayloadCarriesTitleBodyAndSession()
    {
        string json = PushSender.BuildPayload("Eldran", "Erik tells you: hi", "3-scapes/jocke");
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal("Eldran", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Erik tells you: hi", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("3-scapes/jocke", doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task NotifyingWithNoSubscribersIsHarmless()
    {
        PushSender sender = MakeSender(HttpStatusCode.OK, _ => { }, out _);
        var notifier = new PushNotifier(new PushStore(), sender);

        // Record equality: nothing delivered, nothing pruned, nothing failed, no error.
        Assert.Equal(new PushOutcome(0, 0, 0, null),
                     await notifier.NotifyAsync("t", "b", null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AFailedDeliveryNamesItsReason()
    {
        // The whole point of PushOutcome: a rejection must surface as a sentence the test
        // command can print, not vanish into a counter.
        PushSender sender = MakeSender(HttpStatusCode.Forbidden, _ => { }, out _);
        var store = new PushStore();
        store.Add(Subscription());
        var notifier = new PushNotifier(store, sender);

        PushOutcome outcome = await notifier.NotifyAsync("t", "b", null, DateTimeOffset.UtcNow);

        Assert.Equal(1, outcome.Failed);
        Assert.NotNull(outcome.LastError);
        Assert.Contains("403", outcome.LastError);
        Assert.Contains("web.push.apple.com", outcome.LastError);
        Assert.Contains("failed", outcome.ToString());
        Assert.Equal(1, store.Count);   // a 403 is our fault (VAPID/clock), not the device's — keep it
    }

    // ---- helpers -------------------------------------------------------------

    private const string RfcUaPublicKey = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string RfcAuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";

    private static PushSubscription Subscription() =>
        new("https://web.push.apple.com/device1", RfcUaPublicKey, RfcAuthSecret);

    private PushSender MakeSender(HttpStatusCode status, Action<HttpRequestMessage> observe, out VapidKeys vapid)
    {
        vapid = VapidKeys.LoadOrCreate(TempFile("vapid"));
        return new PushSender(vapid, new HttpClient(new StubHandler(status, observe)));
    }

    private static string DecodeJwtPart(string authorizationHeader, int index)
    {
        string jwt = authorizationHeader.Split("t=")[1].Split(',')[0].Trim();
        return Encoding.UTF8.GetString(WebPushCrypto.FromBase64Url(jwt.Split('.')[index]));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Action<HttpRequestMessage> _observe;

        public StubHandler(HttpStatusCode status, Action<HttpRequestMessage> observe)
        {
            _status = status;
            _observe = observe;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Force the content to materialise so encryption actually runs.
            if (request.Content is not null) await request.Content.ReadAsByteArrayAsync(cancellationToken);
            _observe(request);
            return new HttpResponseMessage(_status);
        }
    }
}
