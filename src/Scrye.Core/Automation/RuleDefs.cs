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
