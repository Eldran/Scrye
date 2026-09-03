using System.Text.Json;

namespace Scrye.Core.Gmcp;

/// <summary>
/// The server's reboot countdown, from <c>Mud.Status</c> (3Scapes, subscribed as <c>"Mud 1"</c>
/// since 2 Sep 2026). The package arrives as one snapshot — <c>{"full":1, "uptime",
/// "reboot_left", "reboot_total", "lag"}</c> — and then a delta of <c>uptime</c> and
/// <c>reboot_left</c> roughly every two minutes. Between deltas the clock counts down on the
/// session's own seconds, so what the status row shows is never two minutes stale.
///
/// <para>Two warnings, once each per countdown: at thirty minutes and at five. A long farm run
/// or an unattended trade loop is exactly the thing a reboot interrupts, and the server's own
/// broadcast is a line in the scrollback nobody watching a phone sees — the warning goes out as
/// a notify, which is what reaches the phone. A <c>reboot_left</c> that JUMPS UP means the
/// reboot happened and the next countdown began, so the warnings re-arm.</para>
///
/// <para>Deliberately owns no clock of its own: the session feeds it <see cref="Tick"/> from the
/// same accumulated one-second clock the idle guard runs on, so the tests drive it the same way
/// and no wall-clock ever has to be mocked.</para>
/// </summary>
public sealed class RebootClock
{
    /// <summary>The warning thresholds, in seconds, largest first. Each fires once per countdown.</summary>
    public static readonly int[] WarnAt = { 30 * 60, 5 * 60 };

    private double _now;              // session seconds, advanced by Tick
    private double _observedAt;       // _now when the last reboot_left arrived
    private long _leftAtObserve;      // that reboot_left
    private int _nextWarn;            // index into WarnAt of the next warning still to fire

    /// <summary>True once any <c>Mud.Status</c> carrying <c>reboot_left</c> has arrived.</summary>
    public bool Known { get; private set; }

    /// <summary><c>reboot_total</c>, the length of a full cycle, when the snapshot carried it.</summary>
    public long? Total { get; private set; }

    /// <summary>The server's <c>uptime</c> at the last message, in seconds.</summary>
    public long? Uptime { get; private set; }

    /// <summary>Seconds until the reboot as of now: the last <c>reboot_left</c> less the seconds
    /// ticked since it arrived, floored at zero. Zero when nothing is known.</summary>
    public long SecondsLeft => !Known ? 0 : Math.Max(0, _leftAtObserve - (long)Math.Floor(_now - _observedAt));

    /// <summary>Feed a <c>Mud.Status</c> payload. Anything without a numeric <c>reboot_left</c> is
    /// ignored (a delta of <c>uptime</c> alone, malformed text).</summary>
    public void Observe(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (doc.RootElement.TryGetProperty("uptime", out JsonElement up) && up.ValueKind == JsonValueKind.Number)
                Uptime = (long)up.GetDouble();
            if (doc.RootElement.TryGetProperty("reboot_total", out JsonElement tot) && tot.ValueKind == JsonValueKind.Number)
                Total = (long)tot.GetDouble();
            if (!doc.RootElement.TryGetProperty("reboot_left", out JsonElement left) || left.ValueKind != JsonValueKind.Number)
                return;

            long secs = (long)left.GetDouble();
            // A countdown that went UP is the next cycle: the reboot happened. Re-arm.
            if (Known && secs > SecondsLeft + 60) _nextWarn = 0;
            // Thresholds already inside the new value are not news worth a buzz on arrival —
            // logging in with twenty minutes left should say so on the status row, not toast.
            // Only a threshold CROSSED while we watch fires; on the first observation the
            // pointer skips past every threshold the countdown is already under.
            if (!Known)
                while (_nextWarn < WarnAt.Length && secs <= WarnAt[_nextWarn]) _nextWarn++;
            _leftAtObserve = secs;
            _observedAt = _now;
            Known = true;
        }
        catch (JsonException) { }
    }

    /// <summary>Advance the local clock by <paramref name="seconds"/>.</summary>
    public void Tick(double seconds) => _now += seconds;

    /// <summary>Forget everything — a new connection is a new server clock.</summary>
    public void Reset()
    {
        Known = false;
        Total = null;
        Uptime = null;
        _leftAtObserve = 0;
        _observedAt = _now;
        _nextWarn = 0;
    }

    /// <summary>The next warning that is due, if any, and consume it. Called once per second
    /// tick by the session; returns null almost always.</summary>
    public string? TakeWarning()
    {
        if (!Known || _nextWarn >= WarnAt.Length) return null;
        long left = SecondsLeft;
        if (left > WarnAt[_nextWarn]) return null;
        // Skip every threshold the countdown has passed since the last tick (a long stall).
        while (_nextWarn < WarnAt.Length && left <= WarnAt[_nextWarn]) _nextWarn++;
        return $"reboot in {Describe(left)}";
    }

    /// <summary>The status-row text: <c>"reboot in 9d 3h"</c>, or empty when unknown.</summary>
    public string StatusText => Known ? "reboot in " + Describe(SecondsLeft) : "";

    /// <summary>Human-sized: days and hours over a day, hours and minutes over an hour, minutes
    /// over a minute, seconds under. Never more than two units — a status row is not a stopwatch.</summary>
    public static string Describe(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long d = seconds / 86400, h = seconds % 86400 / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        if (d > 0) return h > 0 ? $"{d}d {h}h" : $"{d}d";
        if (h > 0) return m > 0 ? $"{h}h {m}m" : $"{h}h";
        if (m > 0) return $"{m}m";
        return $"{s}s";
    }
}
