using Scrye.Core.Automation;

namespace Scrye.Core.Profiles;

public enum LayerKind { Global, Mud, Account, Character }

/// <summary>
/// One layer in the configuration cascade (Global -> MUD -> Account -> Character).
/// Scalar settings are NULLABLE — null means "inherit from the parent layer";
/// a set value overrides. Collections (triggers/aliases/timers/variables) MERGE
/// across layers by name, with deeper layers overriding same-named items or
/// listing names in <see cref="Suppress"/> to drop inherited ones. See
/// scrye-profile-model.md.
/// </summary>
public sealed class ProfileLayer
{
    public LayerKind Kind { get; set; } = LayerKind.Character;
    public string Name { get; set; } = "";

    // ---- connection scalars (typically MUD layer) ----
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseTls { get; set; }
    public bool? AcceptInvalidCertificates { get; set; }
    public string? EncodingName { get; set; }
    public int? TerminalColumns { get; set; }
    public int? TerminalRows { get; set; }
    public bool? EnableMip { get; set; }
    public string? MipClientId { get; set; }
    public bool? EnableMxp { get; set; }
    public bool? EnableMsp { get; set; }

    // ---- account / character scalars ----
    public string? Username { get; set; }
    /// <summary>Reference into the OS secret store. The password itself is never
    /// stored in the profile file. (Resolution deferred.)</summary>
    public string? PasswordRef { get; set; }

    // ---- app-level scalars (typically Global layer) ----
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? Theme { get; set; }
    /// <summary>ANSI 16-colour palette: "modern" (xterm/VGA, default) or "classic" (MUSHclient).</summary>
    public string? AnsiPalette { get; set; }

    /// <summary>Leave the command in the input box after Enter instead of clearing it, with the
    /// text selected — so Enter alone repeats it and the next keystroke replaces it. What
    /// MUSHclient and Mudlet both offer. Null/false clears, which is what someone who has not
    /// asked for this expects.</summary>
    public bool? KeepInputAfterSend { get; set; }

    /// <summary>Dead-man's switch for unattended automation: stop when nobody has done anything
    /// for <see cref="IdleGuardSeconds"/>. Null inherits; see
    /// <see cref="Scrye.Core.Session.IdleGuard"/>. Off unless asked for.</summary>
    public bool? IdleGuard { get; set; }

    /// <summary>Idle limit in seconds, clamped to 60..7200. Null inherits, then defaults to 600.</summary>
    public int? IdleGuardSeconds { get; set; }

    /// <summary>Start a transcript automatically on connect. Null inherits; see
    /// <see cref="Scrye.Core.Model.WorldProfile.AutoLog"/>. Set it on a Character layer to log
    /// that character's sessions only.</summary>
    public bool? AutoLog { get; set; }

    /// <summary>"text" or "html" for the automatic transcript. Null inherits, then defaults to
    /// text.</summary>
    public string? AutoLogFormat { get; set; }

    /// <summary>Chat channels this world may relay into whichever world tab is in front, so a
    /// tell on one MUD reaches you while you are playing another. Comma-separated channel names,
    /// <c>"*"</c> for all, empty for none. Null inherits; see
    /// <see cref="Scrye.Core.Model.WorldProfile.RelayChannels"/>, which defaults to tells only.
    /// Set it on the Global layer to change the default for every world at once.</summary>
    public string? RelayChannels { get; set; }

    // ---- collections, merged by name ----
    public List<TriggerDef> Triggers { get; set; } = new();
    public List<AliasDef> Aliases { get; set; } = new();
    public List<TimerDef> Timers { get; set; } = new();
    public List<SequenceSpec> Sequences { get; set; } = new();
    /// <summary>Keyboard macros (key gesture → command). Merged by gesture across layers.</summary>
    public List<MacroDef> Macros { get; set; } = new();

    /// <summary>Ids of plugins enabled at this layer. Plugins are OPT-IN: a plugin loads
    /// for a world only if its id appears in the resolved (unioned) set for that world's
    /// layer chain. Empty everywhere = no plugins load. The plugin manager writes to the
    /// connected node's layer, so a choice sticks to that character (or account/MUD).</summary>
    public List<string> Plugins { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>Names of inherited rules/variables to drop at this layer (tombstones).</summary>
    public List<string> Suppress { get; set; } = new();
}
