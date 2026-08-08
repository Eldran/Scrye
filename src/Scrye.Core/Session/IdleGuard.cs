namespace Scrye.Core.Session;

/// <summary>What a <see cref="IdleGuard.Tick"/> decided this second.</summary>
public enum IdleGuardSignal
{
    /// <summary>Nothing to do.</summary>
    None,
    /// <summary>The grace warning — the guard is close to firing and one keystroke resets it.</summary>
    Warning,
    /// <summary>The guard fired: the session has been unattended past its limit.</summary>
    Fired,
}

/// <summary>
/// A dead-man's switch for unattended automation. It answers one question — "is anyone still
/// here?" — and the only evidence it accepts is the user doing something. Output from the MUD
/// never counts, because a bot walking an area produces output all night; that is precisely the
/// situation this exists to end.
///
/// <para><b>Why it belongs in the client.</b> The MUSHclient original lived inside the area-bot
/// plugin and reached across to switch off the chaos-sea plugin by name. Every plugin that
/// automates anything wants this, and none of them should be reimplementing a clock or knowing
/// each other's ids. Here the session owns the clock and everyone downstream is told.</para>
///
/// <para><b>It fires once per idle stretch</b>, not once per tick. After <see cref="Poke"/> the
/// guard re-arms and can warn and fire again. That matters because firing is loud and because a
/// plugin's idle handler should not have to guard against being called sixty times a minute.</para>
///
/// <para>Pure and clock-free: it advances only when <see cref="Tick"/> is called, so the session
/// loop drives it and a test can drive it a thousand seconds in a millisecond.</para>
/// </summary>
public sealed class IdleGuard
{
    /// <summary>Floor for <see cref="Seconds"/>. Below a minute this stops being a safety net and
    /// starts being a nuisance that fires while you read a room description.</summary>
    public const int MinSeconds = 60;

    /// <summary>Ceiling for <see cref="Seconds"/> — two hours, matching the original.</summary>
    public const int MaxSeconds = 7200;

    /// <summary>Ten minutes, the original's default.</summary>
    public const int DefaultSeconds = 600;

    /// <summary>How far into the idle stretch the warning lands. At 0.8 a ten-minute guard warns
    /// with two minutes left — long enough to notice and type something, short enough that it is
    /// not just a second alarm.</summary>
    private const double WarnFraction = 0.8;

    private int _seconds = DefaultSeconds;
    private bool _enabled;
    private double _idle;
    private bool _warned;
    private bool _fired;

    /// <summary>
    /// Whether the guard is running. Off by default: a client that silently stopped your
    /// automation after ten minutes without being asked would be a bug, not a feature.
    /// Switching it on re-arms it, so enabling mid-session never fires immediately on the
    /// strength of idle time you accrued while it was off.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Poke();
        }
    }

    /// <summary>Idle limit in seconds, clamped to [<see cref="MinSeconds"/>,
    /// <see cref="MaxSeconds"/>]. Changing it re-arms the guard, because a warning already shown
    /// against the old limit says nothing true about the new one.</summary>
    public int Seconds
    {
        get => _seconds;
        set
        {
            int clamped = value < MinSeconds ? MinSeconds : value > MaxSeconds ? MaxSeconds : value;
            if (clamped == _seconds) return;
            _seconds = clamped;
            Poke();
        }
    }

    /// <summary>Seconds since the user last did anything.</summary>
    public double IdleSeconds => _idle;

    /// <summary>Seconds until the guard fires, or 0 once it has. Meaningless while disabled.</summary>
    public double SecondsRemaining => _fired ? 0 : Math.Max(0, _seconds - _idle);

    /// <summary>True between firing and the next <see cref="Poke"/> — i.e. "automation is stopped
    /// and we are waiting for a sign of life". The status line reads this.</summary>
    public bool HasFired => _fired;

    /// <summary>The user did something. Resets the clock and re-arms both the warning and the
    /// firing, so the next idle stretch is judged on its own.</summary>
    public void Poke()
    {
        _idle = 0;
        _warned = false;
        _fired = false;
    }

    /// <summary>
    /// Advance by <paramref name="dtSeconds"/> and report what that crossed. Returns
    /// <see cref="IdleGuardSignal.Fired"/> at most once per idle stretch, and
    /// <see cref="IdleGuardSignal.Warning"/> at most once before it.
    /// </summary>
    public IdleGuardSignal Tick(double dtSeconds)
    {
        // While off the clock does not merely stop, it resets: otherwise switching the guard on
        // after a long quiet spell would fire on history rather than on the here and now.
        if (!_enabled) { _idle = 0; return IdleGuardSignal.None; }
        if (_fired) return IdleGuardSignal.None;
        if (dtSeconds > 0) _idle += dtSeconds;

        // Firing is checked first so a large step (a laptop resuming from sleep, a debugger
        // pause) lands on the outcome that matters rather than announcing a warning it has
        // already blown past.
        if (_idle >= _seconds)
        {
            _fired = true;
            _warned = true;
            return IdleGuardSignal.Fired;
        }
        if (!_warned && _idle >= _seconds * WarnFraction)
        {
            _warned = true;
            return IdleGuardSignal.Warning;
        }
        return IdleGuardSignal.None;
    }

    /// <summary>A short "9m30s" / "45s" for the warning text and the status line.</summary>
    public static string Describe(double seconds)
    {
        int total = (int)Math.Round(seconds);
        if (total < 60) return total + "s";
        int m = total / 60, s = total % 60;
        return s == 0 ? m + "m" : $"{m}m{s}s";
    }
}
