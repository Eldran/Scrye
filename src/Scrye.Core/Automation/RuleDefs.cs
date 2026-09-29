namespace Scrye.Core.Automation;

/// <summary>A trigger: pattern-matched reaction to a line of MUD output.
/// Immutable config; the engine holds compiled/runtime state separately.
/// <see cref="Name"/> is the stable id (for override/delete and the profile
/// cascade), <see cref="Enabled"/> the initial state, <see cref="Source"/> the
/// origin layer (Global/MUD/Account/Character) — see the profile-model doc.</summary>
public sealed record TriggerDef
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
    public bool IsRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool Enabled { get; init; } = true;

    /// <summary>Continue evaluating later triggers after this one matches.</summary>
    public bool KeepEvaluating { get; init; }
    public bool OneShot { get; init; }
    public bool Temporary { get; init; }

    /// <summary>Lower runs first.</summary>
    public int Sequence { get; init; } = 100;
    public string? Group { get; init; }

    public SendTo SendTo { get; init; } = SendTo.World;
    /// <summary>Text template; supports %0..%9, %&lt;name&gt; wildcards and ${var}.</summary>
    public string? Send { get; init; }
    /// <summary>Target variable name when <see cref="SendTo"/> is Variable.</summary>
    public string? Variable { get; init; }
    /// <summary>Script function to call on match.</summary>
    public string? Script { get; init; }

    /// <summary>Route the matched line to this named capture pane (e.g. "Chats").
    /// The pane is created on first use; null = no routing.</summary>
    public string? CapturePane { get; init; }
    /// <summary>Hide the matched line from the main output (display/transcript only —
    /// events, other triggers, and sequences still see it).</summary>
    public bool Gag { get; init; }
    /// <summary>Raise a notification (toast + taskbar flash when unfocused) with the matched line.</summary>
    public bool Notify { get; init; }
    /// <summary>Sound to play on match: "beep", an absolute path, or a file name
    /// resolved under the Scrye sounds folder. Null = silent.</summary>
    public string? Sound { get; init; }

    /// <summary>Recolour the matched line when set ("#RRGGBB"). Null = no highlight.</summary>
    public string? HighlightFore { get; init; }
    /// <summary>Optional highlight background ("#RRGGBB"). Null = leave background as-is.</summary>
    public string? HighlightBack { get; init; }
    /// <summary>When highlighting, recolour the WHOLE line (true, default) or just the
    /// matched text (false). Only meaningful when <see cref="HighlightFore"/>/<see cref="HighlightBack"/> is set.</summary>
    public bool HighlightWholeLine { get; init; } = true;

    /// <summary>
    /// How many lines the pattern is matched against: 1 (the default) is the line that just
    /// arrived; N is that line and the N-1 before it, joined with <c>\n</c>. A pattern typed on
    /// several lines raises this on its own (<see cref="EffectiveLines"/>), so a two-line
    /// pattern needs no setting. The match must reach into the NEWEST line, so a block fires
    /// once, as its last line arrives - not again while it is still in the window.
    /// </summary>
    public int Lines { get; init; } = 1;

    /// <summary>Most lines a trigger may look back over.</summary>
    public const int MaxLines = 50;

    /// <summary>The lines this trigger actually matches over: <see cref="Lines"/>, or the
    /// number of lines its pattern spans when that is more - a pattern with a newline in it (or,
    /// for a regex, a <c>\n</c>) cannot match inside one line. Capped at <see cref="MaxLines"/>.</summary>
    public int EffectiveLines => LinesFor(Pattern, IsRegex, Lines);

    /// <summary>The lines a pattern matches over, given the lines asked for: the same rule as
    /// <see cref="EffectiveLines"/>, for callers without a TriggerDef (plugin triggers).</summary>
    public static int LinesFor(string pattern, bool isRegex, int lines)
    {
        int spans = 1;
        foreach (char c in pattern) if (c == '\n') spans++;
        if (isRegex)
        {
            int escaped = 0;
            for (int i = 0; i + 1 < pattern.Length; i++)
                if (pattern[i] == '\\' && pattern[i + 1] == 'n') { escaped++; i++; }
            spans = Math.Max(spans, escaped + 1);
        }
        return Math.Clamp(Math.Max(lines, spans), 1, MaxLines);
    }

    /// <summary>Profile layer this came from (cascade bookkeeping; informational).</summary>
    public string? Source { get; init; }
}

/// <summary>An alias: pattern-matched reaction to user input before it is sent.
/// Same shape as <see cref="TriggerDef"/> but matched against typed commands.</summary>
public sealed record AliasDef
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
    public bool IsRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool Enabled { get; init; } = true;

    public bool KeepEvaluating { get; init; }
    public bool OneShot { get; init; }
    public bool Temporary { get; init; }

    public int Sequence { get; init; } = 100;
    public string? Group { get; init; }

    public SendTo SendTo { get; init; } = SendTo.World;
    public string? Send { get; init; }
    public string? Variable { get; init; }
    public string? Script { get; init; }

    public string? Source { get; init; }
}

/// <summary>A timer: fires on an interval (and optionally once). Time-of-day
/// timers come later; this is the interval / DoAfter form.</summary>
public sealed record TimerDef
{
    public string Name { get; init; } = "";
    public double IntervalSeconds { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool OneShot { get; init; }
    public string? Group { get; init; }

    public SendTo SendTo { get; init; } = SendTo.World;
    public string? Send { get; init; }
    public string? Variable { get; init; }
    public string? Script { get; init; }

    public string? Source { get; init; }
}

/// <summary>A keyboard macro: a key gesture bound to a command template. Sent to the
/// world (multi-line = one command per line, like a trigger's Send) with ${var}
/// expansion. The gesture string is the identity, so binding the same key again
/// replaces it. Examples of <see cref="Key"/>: "F1", "Ctrl+K", "Shift+F2", "NumPad1".</summary>
public sealed record MacroDef
{
    /// <summary>The key gesture, e.g. "F1", "Ctrl+Shift+K", "NumPad0". Case-insensitive;
    /// modifier order does not matter (normalised by the app's key handler).</summary>
    public string Key { get; init; } = "";

    /// <summary>Command template to send; supports ${var} and multi-line (one command per line).</summary>
    public string Send { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>Profile layer this came from (cascade bookkeeping; informational).</summary>
    public string? Source { get; init; }
}
