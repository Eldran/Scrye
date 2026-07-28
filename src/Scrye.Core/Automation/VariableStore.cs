namespace Scrye.Core.Automation;

/// <summary>Per-world named string variables. Persisted with the character
/// profile / state file (later); for now an in-memory map.</summary>
public sealed class VariableStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;
    public void Set(string name, string value) => _values[name] = value;
    public bool Delete(string name) => _values.Remove(name);
    public int Count => _values.Count;
    public IReadOnlyDictionary<string, string> All => _values;
}
