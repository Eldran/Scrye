using Scrye.Core.Automation;
using Scrye.Core.Model;

namespace Scrye.Core.Profiles;

/// <summary>
/// Folds a layer chain (shallow -> deep) into an <see cref="EffectiveProfile"/>.
/// Scalars: the deepest layer that sets a value wins (null inherits). Collections:
/// union across layers keyed by name; a deeper layer with the same name overrides,
/// and a layer's <see cref="ProfileLayer.Suppress"/> names drop inherited entries
/// BEFORE that layer's own additions.
/// </summary>
public static class ProfileResolver
{
    public static EffectiveProfile Resolve(IReadOnlyList<ProfileLayer> chain)
    {
        var world = new WorldProfile();
        string? font = null, theme = null, user = null, passwordRef = null;
        double? fontSize = null;
        bool idleGuard = false;                                       // off unless a layer asks
        int idleSeconds = Scrye.Core.Session.IdleGuard.DefaultSeconds;
        string displayName = "";

        var triggers = new Dictionary<string, TriggerDef>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, AliasDef>(StringComparer.Ordinal);
        var timers = new Dictionary<string, TimerDef>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, SequenceSpec>(StringComparer.Ordinal);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        // Macros key on their gesture (case-insensitive), not a name — a deeper layer
        // rebinding the same key overrides the inherited macro.
        var macros = new Dictionary<string, MacroDef>(StringComparer.OrdinalIgnoreCase);
        // Opt-in plugins: union of the ids each layer enables (order-preserving), with
        // Suppress able to drop an inherited one.
        var plugins = new List<string>();
        int anon = 0;

        foreach (ProfileLayer layer in chain)
        {
            if (layer.Host is not null) world.Host = layer.Host;
            if (layer.Port is not null) world.Port = layer.Port.Value;
            if (layer.UseTls is not null) world.UseTls = layer.UseTls.Value;
            if (layer.AcceptInvalidCertificates is not null) world.AcceptInvalidCertificates = layer.AcceptInvalidCertificates.Value;
            if (layer.EncodingName is not null) world.EncodingName = layer.EncodingName;
            if (layer.TerminalColumns is not null) world.TerminalColumns = layer.TerminalColumns.Value;
            if (layer.TerminalRows is not null) world.TerminalRows = layer.TerminalRows.Value;
            if (layer.EnableGmcp is not null) world.EnableGmcp = layer.EnableGmcp.Value;
            if (layer.EnableMip is not null) world.EnableMip = layer.EnableMip.Value;
            if (layer.MipClientId is not null) world.MipClientId = layer.MipClientId;
            if (layer.EnableMxp is not null) world.EnableMxp = layer.EnableMxp.Value;
            if (layer.EnableMsp is not null) world.EnableMsp = layer.EnableMsp.Value;

            if (layer.IdleGuard is not null) idleGuard = layer.IdleGuard.Value;
            if (layer.IdleGuardSeconds is not null) idleSeconds = layer.IdleGuardSeconds.Value;
            if (layer.RelayChannels is not null) world.RelayChannels = layer.RelayChannels;
            if (layer.AutoLog is not null) world.AutoLog = layer.AutoLog.Value;
            if (layer.AutoLogFormat is not null) world.AutoLogFormat = layer.AutoLogFormat;

            if (layer.FontFamily is not null) font = layer.FontFamily;
            if (layer.FontSize is not null) fontSize = layer.FontSize;
            if (layer.Theme is not null) theme = layer.Theme;
            if (layer.Username is not null) { user = layer.Username; world.Username = layer.Username; }
            if (layer.PasswordRef is not null) passwordRef = layer.PasswordRef;

            if (layer.Kind != LayerKind.Global && !string.IsNullOrEmpty(layer.Name))
                displayName = layer.Name;

            foreach (string name in layer.Suppress)
            {
                triggers.Remove(name); aliases.Remove(name); timers.Remove(name);
                sequences.Remove(name); variables.Remove(name);
                plugins.RemoveAll(p => string.Equals(p, name, StringComparison.Ordinal));
            }
            foreach (TriggerDef t in layer.Triggers) triggers[Key(t.Name, ref anon)] = t;
            foreach (AliasDef a in layer.Aliases) aliases[Key(a.Name, ref anon)] = a;
            foreach (TimerDef tm in layer.Timers) timers[Key(tm.Name, ref anon)] = tm;
            foreach (SequenceSpec s in layer.Sequences) sequences[Key(s.Name, ref anon)] = s;
            foreach (MacroDef mc in layer.Macros) if (!string.IsNullOrWhiteSpace(mc.Key)) macros[mc.Key.Trim()] = mc;
            foreach (string pid in layer.Plugins)
                if (!string.IsNullOrWhiteSpace(pid) && !plugins.Contains(pid.Trim())) plugins.Add(pid.Trim());
            foreach (KeyValuePair<string, string> kv in layer.Variables) variables[kv.Key] = kv.Value;
        }

        world.Name = displayName.Length > 0 ? displayName : (world.Host.Length > 0 ? world.Host : "World");

        return new EffectiveProfile
        {
            World = world,
            Triggers = triggers.Values.ToArray(),
            Aliases = aliases.Values.ToArray(),
            Timers = timers.Values.ToArray(),
            Sequences = sequences.Values.ToArray(),
            Macros = macros.Values.ToArray(),
            EnabledPlugins = plugins.ToArray(),
            Variables = variables,
            IdleGuardEnabled = idleGuard,
            IdleGuardSeconds = idleSeconds,
            FontFamily = font,
            FontSize = fontSize,
            Theme = theme,
            Username = user,
            PasswordRef = passwordRef,
        };
    }

    private static string Key(string name, ref int anon) =>
        string.IsNullOrEmpty(name) ? " anon" + anon++ : name;
}
