namespace Scrye.Core.Plugins;

/// <summary>
/// A tiny scheduler for plugin timers (<c>scrye.after</c>/<c>scrye.every</c>). Pure and
/// engine-agnostic — callbacks are plain <see cref="Action"/>s, so the MoonSharp/JS
/// binding is thin and this logic is unit-testable without a script host. Advanced by
/// <see cref="Tick"/> on the session loop (1s granularity today). A callback may add or
/// cancel timers re-entrantly: newly-added timers do not fire in the same tick, and a
/// cancel takes effect immediately.
/// </summary>
public sealed class TimerWheel
{
    private sealed class Entry
    {
        public int Id;
        public double Interval;
        public double Remaining;
        public bool Repeat;
        public bool Dead;
        public Action Callback = null!;
    }

    private readonly List<Entry> _entries = new();
    private int _next = 1;

    /// <summary>Live (non-cancelled) timer count.</summary>
    public int Count
    {
        get { int n = 0; foreach (Entry e in _entries) if (!e.Dead) n++; return n; }
    }

    /// <summary>Schedule a timer. <paramref name="repeat"/> false = one-shot. Returns its id.</summary>
    public int Add(double seconds, bool repeat, Action callback)
    {
        double s = seconds < 0 ? 0 : seconds;
        var e = new Entry { Id = _next++, Interval = s, Remaining = s, Repeat = repeat, Callback = callback };
        _entries.Add(e);
        return e.Id;
    }

    /// <summary>Cancel a timer by id. Returns false if it was unknown or already done.</summary>
    public bool Cancel(int id)
    {
        foreach (Entry e in _entries)
            if (e.Id == id && !e.Dead) { e.Dead = true; return true; }
        return false;
    }

    /// <summary>Advance all timers by <paramref name="dt"/> seconds, firing those due.</summary>
    public void Tick(double dt)
    {
        int n = _entries.Count;   // timers added by a callback don't fire this tick
        for (int i = 0; i < n; i++)
        {
            Entry e = _entries[i];
            if (e.Dead) continue;
            e.Remaining -= dt;
            if (e.Remaining > 0) continue;

            // Update scheduling state BEFORE invoking, so a re-entrant Cancel(self) sticks.
            if (e.Repeat) e.Remaining += e.Interval > 0 ? e.Interval : dt;
            else e.Dead = true;

            e.Callback();
        }
        _entries.RemoveAll(x => x.Dead);
    }

    public void Clear() => _entries.Clear();
}
