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
