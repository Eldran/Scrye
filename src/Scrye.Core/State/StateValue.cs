using System.Globalization;

namespace Scrye.Core.State;

public enum StateKind { Null, String, Number, Bool }

/// <summary>
/// A single leaf value in the <see cref="StateStore"/>. Immutable. Carries a kind
/// (so a HUD can tell a number from a string) plus a canonical text form, with typed
/// accessors that never throw — a progress bar asks <see cref="AsNumber"/>, a label
/// asks <see cref="ToString"/>. Sourced from GMCP JSON scalars, MIP vitals, or scripts.
/// </summary>
public readonly struct StateValue : IEquatable<StateValue>
{
    public StateKind Kind { get; }
    /// <summary>Canonical text form ("" for null). Numbers use invariant culture.</summary>
    public string Text { get; }

    private StateValue(StateKind kind, string text) { Kind = kind; Text = text; }

    public static readonly StateValue Null = new(StateKind.Null, "");
    public static StateValue Str(string? s) => new(StateKind.String, s ?? "");
    public static StateValue Num(double d) => new(StateKind.Number, d.ToString(CultureInfo.InvariantCulture));
    public static StateValue Boolean(bool b) => new(StateKind.Bool, b ? "true" : "false");

    public bool IsNull => Kind == StateKind.Null;

    public bool TryGetNumber(out double value) =>
        double.TryParse(Text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    /// <summary>The value as a number, or <paramref name="fallback"/> if it isn't numeric.</summary>
    public double AsNumber(double fallback = 0) => TryGetNumber(out double v) ? v : fallback;

    /// <summary>The value as a bool: true for a Bool "true", or a numeric/text "1"/"true".</summary>
    public bool AsBool() =>
        Kind == StateKind.Bool
            ? Text == "true"
            : Text == "1" || string.Equals(Text, "true", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Text;

    public bool Equals(StateValue other) => Kind == other.Kind && Text == other.Text;
    public override bool Equals(object? obj) => obj is StateValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Kind, Text);
    public static bool operator ==(StateValue a, StateValue b) => a.Equals(b);
    public static bool operator !=(StateValue a, StateValue b) => !a.Equals(b);
}
