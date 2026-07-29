namespace Scrye.Core.Events;

/// <summary>
/// A bounded, in-memory ring buffer of the most recent events. This is what the
/// trigger timeline / debugger UI reads: cheap to keep always-on, never grows
/// without bound. For a full, persistent capture use <see cref="SessionRecorder"/>.
/// </summary>
public sealed class EventLog : IEventSink
{
    private readonly SessionEvent[] _ring;
    private int _head;    // index of the next write slot
    private int _count;   // number of live entries (<= capacity)

    public EventLog(int capacity = 5000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new SessionEvent[capacity];
    }

    public int Capacity => _ring.Length;
    public int Count => _count;

    public void OnEvent(SessionEvent ev)
    {
        _ring[_head] = ev;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    /// <summary>The retained events, oldest first. A fresh copy — safe to hold and iterate.</summary>
    public IReadOnlyList<SessionEvent> Snapshot()
    {
        var result = new SessionEvent[_count];
        int start = (_head - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++)
            result[i] = _ring[(start + i) % _ring.Length];
        return result;
    }

    /// <summary>Retained events of one kind, oldest first (e.g. only TriggerMatched for a trigger view).</summary>
    public IReadOnlyList<SessionEvent> Snapshot(SessionEventKind kind)
    {
        var result = new List<SessionEvent>();
        int start = (_head - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++)
        {
            SessionEvent ev = _ring[(start + i) % _ring.Length];
            if (ev.Kind == kind) result.Add(ev);
        }
        return result;
    }

    public void Clear()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _head = 0;
        _count = 0;
    }
}
