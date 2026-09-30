using Scrye.Companion.Protocol;
using Scrye.Companion.Server.Hub;
using Scrye.Companion.Server.Push;
using Scrye.Companion.Server.Sessions;
using Scrye.Core.Automation;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Regression tests for the companion security/robustness review: tapped links take the
/// literal path, subscribe replies cannot be overtaken by live frames, queue overflow asks
/// the device to resync, and push endpoints/payloads are constrained.
/// </summary>
public class ReviewCompanionHardeningTests
{
    private const string World = "3-scapes/jocke";

    // ---- links ---------------------------------------------------------------

    [Fact]
    public async Task ALinkTapIsSubmittedLiterallyNotAsTypedInput()
    {
        var source = new LinkSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new SendLinkMessage(World, ".companion off")));

        Assert.Null(reply);
        Assert.Equal(new[] { ".companion off" }, source.Links.ToArray());
        Assert.Empty(source.Commands);   // never reached the client-command pipeline
    }

    // ---- subscribe ordering --------------------------------------------------

    [Fact]
    public async Task LiveOutputPublishedWhileASnapshotIsBuiltArrivesAfterIt()
    {
        var source = new LinkSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);
        // Simulates the UI thread flushing output between snapshot capture and the reply
        // being queued by the socket reader.
        source.OnSnapshot = () => hub.PublishOutput(Batch(World, "late", 7));

        object? reply = await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new SessionSubscribeMessage(World)));
        sub.PublishReply(reply);

        Assert.True(sub.Outbound.TryRead(out object? first));
        Assert.IsType<SnapshotMessage>(first);
        Assert.True(sub.Outbound.TryRead(out object? second));
        Assert.IsType<OutputBatchMessage>(second);
    }

    // ---- overflow ------------------------------------------------------------

    [Fact]
    public void AnOverflowingQueueCountsDropsAndAsksForOneResync()
    {
        var hub = new CompanionHub(new LinkSource());
        CompanionSubscriber sub = hub.Add("a", false);
        sub.Subscribe(World);

        for (int i = 0; i < CompanionSubscriber.QueueCapacity * 2; i++)
            hub.PublishOutput(Batch(World, $"line{i}", i));

        Assert.True(sub.DroppedFrames > 0);
        int resyncs = 0;
        while (sub.Outbound.TryRead(out object? m))
            if (m is SessionResyncMessage) resyncs++;
        Assert.Equal(1, resyncs);
    }

    // ---- push ----------------------------------------------------------------

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://web.push.apple.com/QK", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://db5p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]      // not https
    [InlineData("https://fcm.googleapis.com:8443/x", false)]           // odd port
    [InlineData("https://127.0.0.1/x", false)]                         // IP literal
    [InlineData("https://localhost/x", false)]
    [InlineData("https://router.lan/admin", false)]                    // not a push service
    [InlineData("https://evilpush.apple.com.example/x", false)]
    [InlineData("not a url", false)]
    public void OnlyKnownPushServicesAreAccepted(string endpoint, bool allowed) =>
        Assert.Equal(allowed, PushSender.IsAllowedEndpoint(endpoint));

    [Fact]
    public async Task APushSubscriptionToALanHostIsRejected()
    {
        var store = new PushStore();
        var hub = new CompanionHub(new LinkSource()) { PushStore = store };
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(sub,
            CompanionJson.Serialize(new PushSubscribeMessage("https://192.168.1.1/x", "pk", "au"))));

        Assert.Equal(CompanionErrorCode.BadRequest, error.Code);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ThePushStoreIsCapped()
    {
        var store = new PushStore();
        for (int i = 0; i < PushStore.MaxSubscriptions; i++)
            Assert.True(store.TryAdd(new PushSubscription($"https://fcm.googleapis.com/{i}", "k", "a")));

        Assert.False(store.TryAdd(new PushSubscription("https://fcm.googleapis.com/one-more", "k", "a")));
        Assert.True(store.TryAdd(new PushSubscription("https://fcm.googleapis.com/0", "k2", "a2")));   // re-subscribe
    }

    [Fact]
    public void AnOversizedPayloadIsTrimmedToValidJson()
    {
        string json = PushSender.BuildPayload("t", new string('é', 10_000), World);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= PushSender.MaxPayloadBytes);
        using var doc = System.Text.Json.JsonDocument.Parse(json);   // throws if cut mid-JSON
        Assert.Equal(World, doc.RootElement.GetProperty("sessionId").GetString());
    }

    // ---- helpers -------------------------------------------------------------

    private static OutputBatchMessage Batch(string session, string text, long firstSequence)
    {
        var builder = new OutputBatchBuilder();
        builder.Add(Line.FromText(text), firstSequence);
        return builder.Build(session);
    }

    private sealed class LinkSource : ICompanionSessionSource
    {
        public List<string> Commands { get; } = new();
        public List<string> Links { get; } = new();
        public Action? OnSnapshot { get; set; }

        public IReadOnlyList<SessionStateMessage> GetSessions() =>
            new[] { new SessionStateMessage(World, true, "Eldran", "3Scapes") };

        public ValueTask<CommandSubmitResult> SubmitCommandAsync(string sessionId, string command, CommandOrigin origin)
        {
            Commands.Add(command);
            return ValueTask.FromResult(CommandSubmitResult.Accepted);
        }

        public ValueTask SubmitLinkAsync(string sessionId, string command)
        {
            Links.Add(command);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> InvokeHudActionAsync(string sessionId, string pluginId, string actionId) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> InvokeHudSubmitAsync(string sessionId, string pluginId, string actionId, string text) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> InvokeHudCellAsync(string sessionId, string pluginId, string actionId,
                                                  int col, int row, string ch) =>
            ValueTask.FromResult(true);

        public ValueTask<SnapshotMessage?> GetSnapshotAsync(string sessionId, int maxLines)
        {
            OnSnapshot?.Invoke();
            var output = new OutputBatchBuilder().Build(sessionId);
            return ValueTask.FromResult<SnapshotMessage?>(new SnapshotMessage(
                sessionId, GetSessions()[0], output,
                Array.Empty<StateUpdateMessage>(), Array.Empty<HudPanelMessage>()));
        }

        public ValueTask<OutputBatchMessage?> TryReplayAsync(string sessionId, long afterSequence) =>
            ValueTask.FromResult<OutputBatchMessage?>(null);
    }
}
