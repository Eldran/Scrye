using Scrye.Core.Automation;
using Scrye.Core.Model;

namespace Scrye.Core.Profiles;

/// <summary>The flat, resolved configuration a <c>MudSession</c> consumes — the
/// output of folding a layer chain. The engine never sees layers.</summary>
public sealed class EffectiveProfile
{
    public WorldProfile World { get; init; } = new();

    public IReadOnlyList<TriggerDef> Triggers { get; init; } = Array.Empty<TriggerDef>();
    public IReadOnlyList<AliasDef> Aliases { get; init; } = Array.Empty<AliasDef>();
    public IReadOnlyList<TimerDef> Timers { get; init; } = Array.Empty<TimerDef>();
    public IReadOnlyList<SequenceSpec> Sequences { get; init; } = Array.Empty<SequenceSpec>();
    public IReadOnlyList<MacroDef> Macros { get; init; } = Array.Empty<MacroDef>();

    /// <summary>Plugin ids enabled for this world (opt-in; union across the layer chain,
    /// minus any dropped via <see cref="ProfileLayer.Suppress"/>). Empty = no plugins load.</summary>
    public IReadOnlyList<string> EnabledPlugins { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }
    public string? Theme { get; init; }
    public string? Username { get; init; }
    public string? PasswordRef { get; init; }
}
