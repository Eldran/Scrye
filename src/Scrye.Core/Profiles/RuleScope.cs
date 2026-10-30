using Scrye.Core.Automation;

namespace Scrye.Core.Profiles;

/// <summary>
/// Who a rule is for. Where a rule SITS already says most of it - a trigger on the Global
/// layer reaches every world, one on an account reaches every character in it, one on a
/// character reaches that character - but "these two of my three characters" had no home
/// short of copying the rule onto each. A rule's <c>OnlyFor</c> (a comma-separated list of
/// character names on <see cref="TriggerDef"/>, <see cref="AliasDef"/>, <see cref="TimerDef"/>,
/// <see cref="SequenceSpec"/> and <see cref="MacroDef"/>) narrows it to those characters,
/// wherever it sits. Empty is everyone, so every rule written before this keeps its reach.
///
/// <para>A rule narrowed to someone else is left out of the chain entirely, as if it were
/// not on its layer: it neither runs nor overrides a same-named rule from a shallower layer.
/// A chain resolved WITHOUT a character (a tab connected to an account or a bare MUD) has
/// nobody to match, so narrowed rules stay out of it too.</para>
/// </summary>
public static class RuleScope
{
    /// <summary>The names in an OnlyFor list: split on commas, trimmed, blanks and repeats
    /// (case-blind) dropped, in the order written.</summary>
    public static IReadOnlyList<string> Names(string? onlyFor)
    {
        if (string.IsNullOrWhiteSpace(onlyFor)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (string part in onlyFor.Split(','))
        {
            string n = part.Trim();
            if (n.Length == 0 || names.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) continue;
            names.Add(n);
        }
        return names;
    }

    /// <summary>The canonical form to store: "Eldran, Nille", or null for everyone.</summary>
    public static string? Normalize(string? onlyFor)
    {
        IReadOnlyList<string> names = Names(onlyFor);
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Does a rule with this OnlyFor apply to <paramref name="character"/>? Everyone's
    /// rule always does; a narrowed rule only to a character on its list (case-blind, as the
    /// MUD treats names), and never to a chain with no character.</summary>
    public static bool Allows(string? onlyFor, string? character)
    {
        IReadOnlyList<string> names = Names(onlyFor);
        if (names.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(character)) return false;
        string c = character.Trim();
        foreach (string n in names)
            if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// A layer a rule can live on, for moving one there: the Global layer, a MUD, an account
/// on a MUD, or a character (on an account, or directly on a MUD when <see cref="Account"/>
/// is null). <see cref="Label"/> says what it reaches, for a picker.
/// </summary>
public sealed record RuleHome(LayerKind Kind, string? Mud = null, string? Account = null, string? Character = null)
{
    public static RuleHome Global { get; } = new(LayerKind.Global);

    public string Label => Kind switch
    {
        LayerKind.Global => "Global - every world",
        LayerKind.Mud => $"MUD {Mud} - everyone on it",
        LayerKind.Account => $"Account {Account} ({Mud}) - all its characters",
        _ => Account is null ? $"Character {Character} ({Mud})" : $"Character {Character} ({Account}, {Mud})",
    };

    public override string ToString() => Label;
}

/// <summary>What happened to one moved rule: its name where it landed (a clash with a rule
/// already there is renamed "name (2)"; a macro, whose key IS its identity, replaces the
/// one bound to the same key), and where that was.</summary>
public sealed record RuleMoved(RuleHome Home, string Name, bool Renamed, bool Replaced);

/// <summary>Adding a rule to a layer, for moving it between layers.</summary>
public static class RuleMove
{
    /// <summary>Add <paramref name="rule"/> (a TriggerDef, AliasDef, TimerDef, SequenceSpec or
    /// MacroDef) to <paramref name="layer"/>. A name already used there is renamed "name (2)",
    /// "name (3)"... so nothing on the target is overwritten; a macro replaces the one bound to
    /// the same key, since two bindings of one key cannot both work. Also lifts the name out
    /// of the layer's Suppress list - a rule moved onto a layer that hid its name would
    /// otherwise land and stay hidden.</summary>
    public static RuleMoved Add(ProfileLayer layer, RuleHome home, object rule)
    {
        switch (rule)
        {
            case TriggerDef t:
            {
                string n = Unique(t.Name, layer.Triggers.Select(x => x.Name));
                layer.Triggers.Add(t with { Name = n });
                layer.Suppress.Remove(n);
                return new RuleMoved(home, n, n != t.Name, false);
            }
            case AliasDef a:
            {
                string n = Unique(a.Name, layer.Aliases.Select(x => x.Name));
                layer.Aliases.Add(a with { Name = n });
                layer.Suppress.Remove(n);
                return new RuleMoved(home, n, n != a.Name, false);
            }
            case TimerDef tm:
            {
                string n = Unique(tm.Name, layer.Timers.Select(x => x.Name));
                layer.Timers.Add(tm with { Name = n });
                layer.Suppress.Remove(n);
                return new RuleMoved(home, n, n != tm.Name, false);
            }
            case SequenceSpec sq:
            {
                string n = Unique(sq.Name, layer.Sequences.Select(x => x.Name));
                layer.Sequences.Add(sq with { Name = n });
                layer.Suppress.Remove(n);
                return new RuleMoved(home, n, n != sq.Name, false);
            }
            case MacroDef m:
            {
                int removed = layer.Macros.RemoveAll(x =>
                    string.Equals(x.Key.Trim(), m.Key.Trim(), StringComparison.OrdinalIgnoreCase));
                layer.Macros.Add(m);
                return new RuleMoved(home, m.Key, false, removed > 0);
            }
            default:
                throw new ArgumentException($"not a rule: {rule?.GetType().Name ?? "null"}", nameof(rule));
        }
    }

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)"... - the first not in
    /// <paramref name="taken"/>. Names are case-sensitive in the cascade, so the check is too.</summary>
    public static string Unique(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.Ordinal);
        if (!set.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (!set.Contains(candidate)) return candidate;
        }
    }
}
