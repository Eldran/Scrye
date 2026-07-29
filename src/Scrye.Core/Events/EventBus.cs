namespace Scrye.Core.Events;

/// <summary>
/// The instrumented spine of a session. Everything worth observing is funnelled
/// through <see cref="Emit"/>, which stamps a monotonic sequence number and a
/// timestamp and fans the event out to every registered <see cref="IEventSink"/>
/// plus the <see cref="Emitted"/> convenience event (for UI subscribers).
///
/// Single-threaded by contract: only the session's mailbox loop calls
/// <see cref="Emit"/>, so no locking is needed. The <see cref="Clock"/> is
/// injectable purely so tests can produce deterministic timestamps.
/// </summary>
public sealed class EventBus
{
    private readonly List<IEventSink> _sinks = new();
    private long _seq;

    /// <summary>Time source. Defaults to wall-clock UTC; overridden in tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Raised after every emission (in addition to the sinks). Handy for UI.</summary>
    public event Action<SessionEvent>? Emitted;

    /// <summary>Number of events emitted so far (== the last sequence number).</summary>
    public long Count => _seq;

    public void Subscribe(IEventSink sink)
    {
        if (!_sinks.Contains(sink)) _sinks.Add(sink);
    }

    public void Unsubscribe(IEventSink sink) => _sinks.Remove(sink);

    /// <summary>Stamp and dispatch an event. Returns the stamped event.</summary>
    public SessionEvent Emit(SessionEventKind kind, string text = "", string? label = null, string? detail = null)
    {
        var ev = new SessionEvent
        {
            Seq = ++_seq,
            TimeUtc = Clock(),
            Kind = kind,
            Text = text,
            Label = label,
            Detail = detail,
        };
        // Index-based loop: a sink must not mutate the sink list mid-dispatch, but
        // this is defensive and avoids allocating an enumerator on a hot path.
        for (int i = 0; i < _sinks.Count; i++) _sinks[i].OnEvent(ev);
        Emitted?.Invoke(ev);
        return ev;
    }
}
