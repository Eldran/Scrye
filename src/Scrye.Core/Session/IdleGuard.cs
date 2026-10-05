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
/// The ways you can show the idle guard you are still there. Which of them count is yours to
/// choose (<see cref="IdleGuard.Sources"/>): someone who only trusts the keyboard can switch the
/// rest off, so a tap on the phone or a command broadcast from another world no longer keeps a
/// bot world awake.
/// </summary>
[Flags]
public enum IdleSource
{
    None = 0,
    /// <summary>A command typed in this world's command line (including <c>.</c> and <c>/</c> lines).</summary>
    Keyboard = 1,
    /// <summary>A macro key.</summary>
    Macro = 2,
    /// <summary>A click on a link in the output (MXP / Pueblo links the MUD sent).</summary>
    OutputLink = 4,
    /// <summary>A click on a link or toggle in a plugin's panel or the Companion panel.</summary>
    PanelLink = 8,
    /// <summary>Anything from the phone: a command, a tapped link, a panel button.</summary>
    Phone = 16,
    /// <summary>A command typed in ANOTHER world with "All" (broadcast) on.</summary>
    Broadcast = 32,
    All = Keyboard | Macro | OutputLink | PanelLink | Phone | Broadcast,
}

/// <summary>Reading and writing <see cref="IdleSource"/> as the profile stores it: a comma list
/// of names ("keyboard, macro"), so a hand-edited profile reads naturally.</summary>
public static class IdleSources
{
    public static readonly (IdleSource Source, string Name, string Label)[] Each =
    {
        (IdleSource.Keyboard,   "keyboard",   "Typing in this world's command line"),
        (IdleSource.Macro,      "macro",      "Macro keys"),
        (IdleSource.OutputLink, "outputlink", "Clicking links in the output"),
        (IdleSource.PanelLink,  "panellink",  "Clicking links and toggles in plugin panels"),
        (IdleSource.Phone,      "phone",      "The phone companion"),
        (IdleSource.Broadcast,  "broadcast",  "Commands broadcast from another world (All)"),
    };

    /// <summary>"keyboard, macro". All sources is "all"; none is "none".</summary>
    public static string Format(IdleSource s)
    {
        if ((s & IdleSource.All) == IdleSource.All) return "all";
        var names = new List<string>();
        foreach (var e in Each) if ((s & e.Source) != 0) names.Add(e.Name);
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>The flags a stored list names; unknown words are ignored, and nothing usable at
    /// all (null, blank, only unknown words) is null so the cascade falls through.</summary>
    public static IdleSource? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        IdleSource s = IdleSource.None;
        bool any = false;
        foreach (string raw in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string w = raw.Trim().ToLowerInvariant();
            if (w == "all") { s = IdleSource.All; any = true; continue; }
            if (w == "none") { any = true; continue; }
            foreach (var e in Each) if (e.Name == w) { s |= e.Source; any = true; }
        }
        return any ? s : null;
    }

    /// <summary>What a source is called in the status line.</summary>
    public static string Describe(IdleSource s)
    {
        foreach (var e in Each) if (e.Source == s) return e.Name switch
        {
            "keyboard" => "typed", "macro" => "macro key", "outputlink" => "output link",
            "panellink" => "panel click", "phone" => "phone", "broadcast" => "broadcast",
            _ => e.Name,
        };
        return s.ToString();
    }
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

    /// <summary>
    /// The hard stop: when the guard fires, also hold every plugin - their timers stop and
    /// nothing they send reaches the MUD - until you are back. On by default. Without it a
    /// plugin that ignores <c>scrye.onIdle</c> (or one somebody wrote without it) keeps its bot
    /// running; with it nothing a plugin does can slip through. Plugins are still TOLD first, so
    /// one that stops itself cleanly does.
    /// </summary>
    public bool HoldPlugins { get; set; } = true;

    /// <summary>Which kinds of activity count as you being here (all of them unless narrowed).</summary>
    public IdleSource Sources { get; set; } = IdleSource.All;

    /// <summary>Whether <paramref name="source"/> counts.</summary>
    public bool Counts(IdleSource source) => (Sources & source) != 0;

    /// <summary>The last activity that counted: what it was, a short detail (the command, the
    /// key), and when. Null until something has. <c>.idle</c> and the Idle menu show it, so a
    /// guard that "reset by itself" names what reset it.</summary>
    public (IdleSource Source, string Detail, DateTimeOffset At)? LastCounted { get; private set; }

    /// <summary>The last activity that did NOT count (its source is switched off), for the same
    /// display: "ignored: phone 'north' 12:04".</summary>
    public (IdleSource Source, string Detail, DateTimeOffset At)? LastIgnored { get; private set; }

    /// <summary>
    /// Something you did, of kind <paramref name="source"/>. When that kind counts this is a
    /// <see cref="Poke"/> and returns true; when it does not, the clock runs on and it returns
    /// false. Either way it is remembered for the display.
    /// </summary>
    public bool NoteActivity(IdleSource source, string? detail, DateTimeOffset now)
    {
        string d = detail ?? "";
        if (d.Length > 40) d = d[..39] + "…";
        if (!Counts(source))
        {
            LastIgnored = (source, d, now);
            return false;
        }
        LastCounted = (source, d, now);
        Poke();
        return true;
    }

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
