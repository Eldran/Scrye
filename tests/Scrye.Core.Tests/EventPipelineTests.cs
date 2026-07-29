using Scrye.Core.Events;
using Xunit;

namespace Scrye.Core.Tests;

public class EventBusTests
{
    [Fact]
    public void EmitStampsMonotonicSequenceFromOne()
    {
        var bus = new EventBus();
        var a = bus.Emit(SessionEventKind.Notice, "a");
        var b = bus.Emit(SessionEventKind.Notice, "b");
        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
        Assert.Equal(2, bus.Count);
    }

    [Fact]
    public void EmitUsesInjectedClock()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bus = new EventBus { Clock = () => t };
        var ev = bus.Emit(SessionEventKind.Connected, "x");
        Assert.Equal(t, ev.TimeUtc);
    }

    [Fact]
    public void SinksReceiveEventsAndUnsubscribeStops()
    {
        var bus = new EventBus();
        var sink = new CountingSink();
        bus.Subscribe(sink);
        bus.Subscribe(sink);              // idempotent
        bus.Emit(SessionEventKind.Notice, "1");
        Assert.Equal(1, sink.Count);      // only once despite double-subscribe
        bus.Unsubscribe(sink);
        bus.Emit(SessionEventKind.Notice, "2");
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void EmittedEventFires()
    {
        var bus = new EventBus();
        SessionEvent? seen = null;
        bus.Emitted += ev => seen = ev;
        bus.Emit(SessionEventKind.Sent, "look", "label", "detail");
        Assert.NotNull(seen);
        Assert.Equal("look", seen!.Text);
        Assert.Equal("label", seen.Label);
        Assert.Equal("detail", seen.Detail);
    }

    private sealed class CountingSink : IEventSink
    {
        public int Count { get; private set; }
        public void OnEvent(SessionEvent ev) => Count++;
    }
}

public class EventLogTests
{
    private static EventBus BusInto(EventLog log)
    {
        var bus = new EventBus();
        bus.Subscribe(log);
        return bus;
    }

    [Fact]
    public void SnapshotIsOldestFirst()
    {
        var log = new EventLog(10);
        var bus = BusInto(log);
        bus.Emit(SessionEventKind.Notice, "a");
        bus.Emit(SessionEventKind.Notice, "b");
        var snap = log.Snapshot();
        Assert.Equal(new[] { "a", "b" }, snap.Select(e => e.Text));
    }

    [Fact]
    public void OverflowDropsOldest()
    {
        var log = new EventLog(3);
        var bus = BusInto(log);
        foreach (var s in new[] { "1", "2", "3", "4", "5" }) bus.Emit(SessionEventKind.Notice, s);
        Assert.Equal(3, log.Count);
        Assert.Equal(new[] { "3", "4", "5" }, log.Snapshot().Select(e => e.Text));
    }

    [Fact]
    public void SnapshotByKindFilters()
    {
        var log = new EventLog(10);
        var bus = BusInto(log);
        bus.Emit(SessionEventKind.LineReceived, "line");
        bus.Emit(SessionEventKind.TriggerMatched, "line", "t1");
        bus.Emit(SessionEventKind.LineReceived, "line2");
        var trig = log.Snapshot(SessionEventKind.TriggerMatched);
        Assert.Single(trig);
        Assert.Equal("t1", trig[0].Label);
    }

    [Fact]
    public void ClearEmptiesTheLog()
    {
        var log = new EventLog(5);
        var bus = BusInto(log);
        bus.Emit(SessionEventKind.Notice, "x");
        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Snapshot());
    }
}

public class SessionRecorderTests
{
    private static SessionRecorder Recorded(out EventBus bus, params (SessionEventKind kind, string text)[] evs)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int i = 0;
        bus = new EventBus { Clock = () => t.AddSeconds(i++) };
        var rec = new SessionRecorder("TestWorld", t);
        bus.Subscribe(rec);
        foreach (var (kind, text) in evs) bus.Emit(kind, text);
        return rec;
    }

    [Fact]
    public void JsonLinesRoundTripPreservesEventsAndHeader()
    {
        var rec = Recorded(out _,
            (SessionEventKind.Connected, "host"),
            (SessionEventKind.LineReceived, "hello"),
            (SessionEventKind.Sent, "look"));
        var parsed = SessionRecorder.Parse(rec.ToJsonLines());

        Assert.Equal("TestWorld", parsed.Header.World);
        Assert.Equal(3, parsed.Events.Count);
        Assert.Equal(SessionEventKind.LineReceived, parsed.Events[1].Kind);
        Assert.Equal("look", parsed.Events[2].Text);
        Assert.Equal(1, parsed.Events[0].Seq);
    }

    [Fact]
    public void DurationSpansFirstToLast()
    {
        var rec = Recorded(out _,
            (SessionEventKind.Connected, "a"),
            (SessionEventKind.Notice, "b"),
            (SessionEventKind.Notice, "c"));
        Assert.Equal(TimeSpan.FromSeconds(2), SessionRecorder.Parse(rec.ToJsonLines()).Duration);
    }

    [Fact]
    public void SaveAndLoadRoundTripsOnDisk()
    {
        var rec = Recorded(out _, (SessionEventKind.Connected, "x"), (SessionEventKind.Notice, "y"));
        string path = Path.Combine(Path.GetTempPath(), "scrye_test_" + Guid.NewGuid().ToString("N") + ".scryerec");
        try
        {
            rec.Save(path);
            var loaded = SessionRecorder.Load(path);
            Assert.Equal(2, loaded.Events.Count);
            Assert.Equal("TestWorld", loaded.Header.World);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ParseSkipsBlankLines()
    {
        var rec = Recorded(out _, (SessionEventKind.Notice, "only"));
        string text = rec.ToJsonLines() + "\n\n  \n";
        Assert.Single(SessionRecorder.Parse(text).Events);
    }
}

public class SessionReplayerTests
{
    private static SessionRecording Make(params SessionEventKind[] kinds)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int i = 0;
        var bus = new EventBus { Clock = () => t.AddMilliseconds(10 * i++) };
        var rec = new SessionRecorder("W", t);
        bus.Subscribe(rec);
        foreach (var k in kinds) bus.Emit(k, k.ToString());
        return new SessionRecording(rec.Header, rec.Events.ToArray());
    }

    [Fact]
    public void ReplayEmitsAllInOrder()
    {
        var rep = new SessionReplayer(Make(
            SessionEventKind.Connected, SessionEventKind.LineReceived, SessionEventKind.Sent));
        var seen = new List<SessionEventKind>();
        rep.Replay(ev => seen.Add(ev.Kind));
        Assert.Equal(new[] { SessionEventKind.Connected, SessionEventKind.LineReceived, SessionEventKind.Sent }, seen);
    }

    [Fact]
    public void ReplayFilterSelectsSubset()
    {
        var rep = new SessionReplayer(Make(
            SessionEventKind.LineReceived, SessionEventKind.Sent, SessionEventKind.LineReceived));
        int lines = 0;
        rep.Replay(_ => lines++, ev => ev.Kind == SessionEventKind.LineReceived);
        Assert.Equal(2, lines);
    }

    [Fact]
    public async Task TimedReplayEmitsEverything()
    {
        var rep = new SessionReplayer(Make(SessionEventKind.LineReceived, SessionEventKind.LineReceived));
        int n = 0;
        await rep.ReplayTimedAsync(_ => n++, speed: 100.0);
        Assert.Equal(2, n);
    }
}
