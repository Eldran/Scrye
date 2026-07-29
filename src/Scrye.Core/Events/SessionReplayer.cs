namespace Scrye.Core.Events;

/// <summary>
/// Plays a <see cref="SessionRecording"/> back through a callback. Two modes:
/// <see cref="Replay"/> fires everything immediately in order (for the timeline
/// scrubber, exports, and tests); <see cref="ReplayTimedAsync"/> reproduces the
/// original inter-event timing, scaled by <c>speed</c>, for a real-time playback.
/// Replay only re-emits captured events — it never touches the network.
/// </summary>
public sealed class SessionReplayer
{
    private readonly SessionRecording _rec;

    /// <summary>Longest gap actually waited between two events, regardless of the
    /// recorded gap — stops a session that idled for an hour from stalling playback.</summary>
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromSeconds(5);

    public SessionReplayer(SessionRecording rec) => _rec = rec;

    public IReadOnlyList<SessionEvent> Events => _rec.Events;

    /// <summary>Emit every event in order, immediately. Optional filter selects a subset.</summary>
    public void Replay(Action<SessionEvent> onEvent, Predicate<SessionEvent>? filter = null)
    {
        foreach (SessionEvent ev in _rec.Events)
            if (filter is null || filter(ev))
                onEvent(ev);
    }

    /// <summary>Emit events reproducing their recorded spacing, scaled by
    /// <paramref name="speed"/> (2.0 = twice as fast). Gaps are clamped to
    /// <see cref="MaxGap"/>.</summary>
    public async Task ReplayTimedAsync(Action<SessionEvent> onEvent, double speed = 1.0,
        CancellationToken ct = default)
    {
        if (speed <= 0) speed = 1.0;
        SessionEvent? prev = null;
        foreach (SessionEvent ev in _rec.Events)
        {
            if (prev is not null)
            {
                TimeSpan gap = ev.TimeUtc - prev.TimeUtc;
                if (gap > TimeSpan.Zero)
                {
                    double ms = gap.TotalMilliseconds / speed;
                    if (ms > MaxGap.TotalMilliseconds) ms = MaxGap.TotalMilliseconds;
                    if (ms >= 1) await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            onEvent(ev);
            prev = ev;
        }
    }
}
