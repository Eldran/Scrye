using Scrye.Companion.Protocol;
using Scrye.Companion.Server.Hub;
using Scrye.Companion.Server.Push;
using Scrye.Companion.Server.Sessions;
using Scrye.Core.Automation;
using Scrye.Core.State;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The hub's client-message dispatch and fan-out.
///
/// <para>Two behaviours are load-bearing and easy to regress. First, <b>every rejection must
/// produce an error frame</b> rather than a silent drop — a client cannot otherwise tell
/// refusal from a dead socket. Second, <b>a resume that cannot be replayed must fall back to
/// a snapshot</b>; serving a partial replay would silently skip lines the client never saw,
/// which is far worse than resending too much.</para>
/// </summary>
public class CompanionHubTests
{
    private const string World = "3-scapes/jocke";

    // ---- fan-out -------------------------------------------------------------

    [Fact]
    public void OutputReachesOnlySubscribersOfThatSession()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber watching = hub.Add("a", mayRunScripts: false);
        CompanionSubscriber elsewhere = hub.Add("b", mayRunScripts: false);
        watching.Subscribe(World);
        elsewhere.Subscribe("other-world");

        hub.PublishOutput(Batch(World, "hello"));

        Assert.True(watching.Outbound.TryRead(out object? got));
        Assert.IsType<OutputBatchMessage>(got);
        Assert.False(elsewhere.Outbound.TryRead(out _));
    }

    [Fact]
    public void ASubscriberWithNoSessionReceivesNothing()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);   // never subscribed

        hub.PublishOutput(Batch(World, "hello"));

        Assert.False(sub.Outbound.TryRead(out _));
    }

    [Fact]
    public void EmptyBatchesAreNotBroadcast()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);
        sub.Subscribe(World);

        hub.PublishOutput(new OutputBatchMessage(World, Array.Empty<StyleDto>(), Array.Empty<OutputLineDto>()));
        hub.PublishPaneOutput(new PaneOutputMessage(World, "Chats", Array.Empty<StyleDto>(), Array.Empty<OutputLineDto>()));

        Assert.False(sub.Outbound.TryRead(out _));
    }

    [Fact]
    public void SessionStateGoesToEveryDeviceRegardlessOfSubscription()
    {
        // Otherwise a phone watching world A never learns that world B came online, and its
        // session picker goes stale.
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);
        sub.Subscribe("some-other-world");

        hub.PublishSessionState(new SessionStateMessage(World, true, "Eldran", "3Scapes"));

        Assert.True(sub.Outbound.TryRead(out _));
    }

    [Fact]
    public void RemovingASubscriberCompletesItsQueue()
    {
        var hub = new CompanionHub(new FakeSource());
        hub.Add("a", false);

        Assert.Equal(1, hub.SubscriberCount);
        hub.Remove("a");
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public void PublishingTracksTheSubscribersCursor()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);
        sub.Subscribe(World);

        hub.PublishOutput(Batch(World, "one", firstSequence: 40));

        Assert.Equal(40, sub.LastSentSequence);
    }

    // ---- dispatch ------------------------------------------------------------

    [Fact]
    public async Task JunkFramesGetAnErrorNotSilence()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(sub, "{not json"));
        Assert.Equal(CompanionErrorCode.BadRequest, error.Code);
    }

    [Fact]
    public async Task UnknownMessageTypesGetAnError()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(
            await hub.HandleClientMessageAsync(sub, """{"type":"nonsense"}"""));
        Assert.Equal(CompanionErrorCode.BadRequest, error.Code);
    }

    [Fact]
    public async Task SubscribingToAnUnknownSessionIsRejected()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SessionSubscribeMessage("no-such-world"))));
        Assert.Equal(CompanionErrorCode.UnknownSession, error.Code);
    }

    [Fact]
    public async Task SubscribingAnswersWithASnapshot()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SessionSubscribeMessage(World)));

        Assert.IsType<SnapshotMessage>(reply);
        Assert.Equal(World, sub.SessionId);
    }

    [Fact]
    public async Task ReplayableResumeReturnsABatch()
    {
        var hub = new CompanionHub(new FakeSource { CanReplay = true });
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SessionResumeMessage(World, 5)));

        Assert.IsType<OutputBatchMessage>(reply);
    }

    [Fact]
    public async Task UnreplayableResumeFallsBackToASnapshot()
    {
        // The safe outcome: a partial replay would drop lines the client never saw.
        var hub = new CompanionHub(new FakeSource { CanReplay = false });
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SessionResumeMessage(World, 0)));

        Assert.IsType<SnapshotMessage>(reply);
    }

    // ---- the scripting gate at the boundary ---------------------------------

    [Fact]
    public async Task OrdinaryCommandsPassAndAreTaggedCompanion()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", mayRunScripts: false);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SendCommandMessage(World, "north")));

        Assert.Null(reply);   // nothing to say on success
        Assert.Equal("north", source.Commands.Single().Command);
        Assert.Equal(CommandSource.Companion, source.Commands.Single().Origin.Source);
    }

    [Fact]
    public async Task ADeviceWithoutScriptingCannotUseTheLuaConsole()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", mayRunScripts: false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SendCommandMessage(World, "/world.Send('x')"))));

        Assert.Equal(CompanionErrorCode.PermissionDenied, error.Code);
        Assert.Empty(source.Commands);   // and it never reached the desktop
    }

    [Fact]
    public async Task AGrantedDeviceMayUseTheLuaConsole()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", mayRunScripts: true);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new SendCommandMessage(World, "/world.Send('x')")));

        Assert.Null(reply);
        Assert.Single(source.Commands);
    }

    // ---- hud actions ---------------------------------------------------------

    [Fact]
    public async Task HudActionReachesTheDesktopWithThePluginIdParsedOut()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new HudActionMessage(World, "3s-raid|Auto Raid", "btn-arm")));

        Assert.Null(reply);
        Assert.Equal(("3s-raid", "btn-arm"), source.HudActions.Single());
    }

    [Fact]
    public async Task AMalformedPanelIdIsRejected()
    {
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new HudActionMessage(World, "no-separator", "btn"))));

        Assert.Equal(CompanionErrorCode.BadRequest, error.Code);
    }

    [Fact]
    public async Task AnUnknownPanelActionReportsBack()
    {
        var hub = new CompanionHub(new FakeSource { HudActionSucceeds = false });
        CompanionSubscriber sub = hub.Add("a", false);

        Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new HudActionMessage(World, "p|Panel", "btn"))));
    }

    [Fact]
    public async Task HudSubmitCarriesTheTypedText()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new HudSubmitMessage(World, "3s-raid|Auto Raid", "in-target", "wiremouth")));

        Assert.Null(reply);
        Assert.Equal(("3s-raid", "in-target", "wiremouth"), source.HudSubmits.Single());
    }

    [Fact]
    public async Task HudCellCarriesTheCoordinatesVerbatim()
    {
        // The plugin maps col/row/ch back to its own data, so the hub must not
        // reinterpret them — a transposed pair here is a wrong map square there.
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);

        object? reply = await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new HudCellMessage(World, "3s-raid|Map", "grid-1", 4, 2, "#")));

        Assert.Null(reply);
        Assert.Equal(("3s-raid", "grid-1", 4, 2, "#"), source.HudCells.Single());
    }

    [Fact]
    public async Task AMalformedPanelIdIsRejectedForSubmitAndCellToo()
    {
        var source = new FakeSource();
        var hub = new CompanionHub(source);
        CompanionSubscriber sub = hub.Add("a", false);

        var onSubmit = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new HudSubmitMessage(World, "no-separator", "in", "x"))));
        var onCell = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new HudCellMessage(World, "no-separator", "grid", 0, 0, "#"))));

        Assert.Equal(CompanionErrorCode.BadRequest, onSubmit.Code);
        Assert.Equal(CompanionErrorCode.BadRequest, onCell.Code);
        Assert.Empty(source.HudSubmits);
        Assert.Empty(source.HudCells);
    }

    // ---- push subscriptions --------------------------------------------------

    [Fact]
    public async Task PushSubscribeStoresTheDevice()
    {
        var store = new PushStore();
        var hub = new CompanionHub(new FakeSource()) { PushStore = store };
        CompanionSubscriber sub = hub.Add("a", false);

        await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new PushSubscribeMessage("https://web.push.apple.com/x", "pk", "au")));

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task PushUnsubscribeForgetsIt()
    {
        var store = new PushStore();
        store.Add(new PushSubscription("https://web.push.apple.com/x", "pk", "au"));
        var hub = new CompanionHub(new FakeSource()) { PushStore = store };
        CompanionSubscriber sub = hub.Add("a", false);

        await hub.HandleClientMessageAsync(sub, CompanionJson.Serialize(
            new PushUnsubscribeMessage("https://web.push.apple.com/x")));

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task AnIncompletePushSubscriptionIsRejected()
    {
        var hub = new CompanionHub(new FakeSource()) { PushStore = new PushStore() };
        CompanionSubscriber sub = hub.Add("a", false);

        var error = Assert.IsType<ErrorMessage>(await hub.HandleClientMessageAsync(
            sub, CompanionJson.Serialize(new PushSubscribeMessage("https://x", "", ""))));

        Assert.Equal(CompanionErrorCode.BadRequest, error.Code);
    }

    // ---- bounded queues ------------------------------------------------------

    [Fact]
    public void ASlowDeviceDropsFramesRatherThanGrowingWithoutBound()
    {
        // A phone on a bad connection must not make the desktop allocate forever during a
        // combat burst. Dropping is recoverable — the client's cursor falls behind and it
        // resumes; unbounded buffering is not.
        var hub = new CompanionHub(new FakeSource());
        CompanionSubscriber sub = hub.Add("a", false);
        sub.Subscribe(World);

        for (int i = 0; i < CompanionSubscriber.QueueCapacity * 2; i++)
            hub.PublishOutput(Batch(World, $"line{i}", firstSequence: i));

        int drained = 0;
        while (sub.Outbound.TryRead(out _)) drained++;

        Assert.True(drained <= CompanionSubscriber.QueueCapacity,
            $"queue held {drained}, cap is {CompanionSubscriber.QueueCapacity}");
    }

    // ---- helpers -------------------------------------------------------------

    private static OutputBatchMessage Batch(string session, string text, long firstSequence = 0)
    {
        var builder = new OutputBatchBuilder();
        builder.Add(Line.FromText(text), firstSequence);
        return builder.Build(session);
    }

    private sealed class FakeSource : ICompanionSessionSource
    {
        public bool CanReplay { get; init; }
        public bool HudActionSucceeds { get; init; } = true;

        public List<(string Command, CommandOrigin Origin)> Commands { get; } = new();
        public List<(string Plugin, string Action)> HudActions { get; } = new();
        public List<(string Plugin, string Action, string Text)> HudSubmits { get; } = new();
        public List<(string Plugin, string Action, int Col, int Row, string Ch)> HudCells { get; } = new();

        public IReadOnlyList<SessionStateMessage> GetSessions() =>
            new[] { new SessionStateMessage(World, true, "Eldran", "3Scapes") };

        public ValueTask<CommandSubmitResult> SubmitCommandAsync(string sessionId, string command, CommandOrigin origin)
        {
            if (!CommandPrivilege.IsPermitted(command, origin))
                return ValueTask.FromResult(CommandSubmitResult.RejectedScriptingNotPermitted);
            Commands.Add((command, origin));
            return ValueTask.FromResult(CommandSubmitResult.Accepted);
        }

        public ValueTask<bool> InvokeHudActionAsync(string sessionId, string pluginId, string actionId)
        {
            HudActions.Add((pluginId, actionId));
            return ValueTask.FromResult(HudActionSucceeds);
        }

        public ValueTask<bool> InvokeHudSubmitAsync(string sessionId, string pluginId, string actionId, string text)
        {
            HudSubmits.Add((pluginId, actionId, text));
            return ValueTask.FromResult(HudActionSucceeds);
        }

        public ValueTask<bool> InvokeHudCellAsync(string sessionId, string pluginId, string actionId,
                                                  int col, int row, string ch)
        {
            HudCells.Add((pluginId, actionId, col, row, ch));
            return ValueTask.FromResult(HudActionSucceeds);
        }

        public ValueTask<SnapshotMessage?> GetSnapshotAsync(string sessionId, int maxLines) =>
            ValueTask.FromResult<SnapshotMessage?>(new SnapshotMessage(
                sessionId,
                GetSessions()[0],
                new OutputBatchMessage(sessionId, Array.Empty<StyleDto>(), Array.Empty<OutputLineDto>()),
                new[] { new StateUpdateMessage(sessionId, "char.name", StateKind.String, "Eldran") },
                Array.Empty<HudPanelMessage>()));

        public ValueTask<OutputBatchMessage?> TryReplayAsync(string sessionId, long afterSequence) =>
            ValueTask.FromResult(CanReplay ? Batch(sessionId, "replayed", afterSequence + 1) : null);
    }
}
