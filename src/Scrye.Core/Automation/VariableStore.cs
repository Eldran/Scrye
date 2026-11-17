namespace Scrye.Core.Automation;

/// <summary>Per-world named string variables. Persisted with the character
/// profile / state file (later); for now an in-memory map.</summary>
public sealed class VariableStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Goes up whenever a value actually changes (set to something different, or
    /// deleted). A trigger whose pattern names a variable checks this before each match and
    /// only looks at its variables again when it moved - MIP rewrites hp/sp on every prompt
    /// with the same numbers, and that must not cost a recompile.</summary>
    public long Version { get; private set; }

    public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;
    public void Set(string name, string value)
    {
        if (_values.TryGetValue(name, out string? old) && string.Equals(old, value, StringComparison.Ordinal)) return;
        _values[name] = value;
        Version++;
    }
    public bool Delete(string name)
    {
        if (!_values.Remove(name)) return false;
        Version++;
        return true;
    }
    public int Count => _values.Count;
    public IReadOnlyDictionary<string, string> All => _values;
}
