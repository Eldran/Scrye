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

    // ---- account / character scalars ----
    public string? Username { get; set; }
    /// <summary>Reference into the OS secret store. The password itself is never
    /// stored in the profile file. (Resolution deferred.)</summary>
    public string? PasswordRef { get; set; }

    // ---- app-level scalars (typically Global layer) ----
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? Theme { get; set; }

    // ---- collections, merged by name ----
    public List<TriggerDef> Triggers { get; set; } = new();
    public List<AliasDef> Aliases { get; set; } = new();
    public List<TimerDef> Timers { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>Names of inherited rules/variables to drop at this layer (tombstones).</summary>
    public List<string> Suppress { get; set; } = new();
}
