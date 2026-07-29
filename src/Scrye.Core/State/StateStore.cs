using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrye.Core.State;

/// <summary>A single change to the state tree (a leaf was set, changed, or removed).</summary>
public readonly record struct StateChange(string Path, StateValue Value, bool Removed);

/// <summary>
/// The structured, watchable game-state model (Foundation B). GMCP/MSDP/MIP land here
/// as a tree of dotted paths — <c>char.vitals.hp</c>, <c>room.exits.0</c> — and any
/// consumer can read a path, take a snapshot, or <see cref="Watch"/> a path/subtree for
/// changes. This is the data layer the HUD binds to and the state inspector reads.
///
/// Stored as a flat path→value map with hierarchical semantics: a watcher on <c>char</c>
/// fires for <c>char.vitals.hp</c> too. Single-threaded by contract — fed and read on the
/// session's mailbox loop; UI/plugin consumers marshal to their own thread.
/// </summary>
public sealed class StateStore
{
    private sealed record Watcher(string Path, Action<string, StateValue> Callback);

    private readonly Dictionary<string, StateValue> _values = new(StringComparer.Ordinal);
    private readonly List<Watcher> _watchers = new();

    /// <summary>Every change, in order — for the inspector and change-logging.</summary>
    public event Action<StateChange>? Changed;

    public int Count => _values.Count;

    /// <summary>Value at <paramref name="path"/>, or <see cref="StateValue.Null"/> if unset.</summary>
    public StateValue Get(string path) =>
        _values.TryGetValue(Normalize(path), out StateValue v) ? v : StateValue.Null;

    public bool Has(string path) => _values.ContainsKey(Normalize(path));

    /// <summary>A copy of the whole tree (path→value), sorted by path — for the inspector.</summary>
    public IReadOnlyList<KeyValuePair<string, StateValue>> Snapshot()
    {
        var list = new List<KeyValuePair<string, StateValue>>(_values);
        list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    /// <summary>Set a leaf. Fires <see cref="Changed"/> and matching watchers only when the value actually changes.</summary>
    public void Set(string path, StateValue value)
    {
        string key = Normalize(path);
        if (_values.TryGetValue(key, out StateValue existing) && existing == value) return;
        _values[key] = value;
        Notify(key, value, removed: false);
    }

    public void Remove(string path)
    {
        string key = Normalize(path);
        if (_values.Remove(key))
            Notify(key, StateValue.Null, removed: true);
    }

    /// <summary>Remove <paramref name="prefix"/> and everything beneath it (<c>prefix.*</c>).
    /// Used before re-applying a GMCP package, which always sends the full object.</summary>
    public void ClearPrefix(string prefix)
    {
        string p = Normalize(prefix);
        var doomed = new List<string>();
        foreach (string key in _values.Keys)
            if (key == p || key.StartsWith(p + ".", StringComparison.Ordinal))
                doomed.Add(key);
        foreach (string key in doomed)
        {
            _values.Remove(key);
            Notify(key, StateValue.Null, removed: true);
        }
    }

    /// <summary>
    /// Merge a GMCP-style JSON payload under <paramref name="prefix"/> (e.g. package
    /// "Char.Vitals" + <c>{"hp":42,"maxhp":100}</c> → <c>char.vitals.hp</c>=42, …).
    /// A package resend replaces the whole object, so keys under the prefix that are NOT
    /// in the new payload are removed — but this diffs rather than clear-then-set, so
    /// unchanged leaves fire nothing and there is no null flicker. Objects nest by key,
    /// arrays by index. Non-JSON payloads set the prefix to the raw text.
    /// </summary>
    public void SetJson(string prefix, string json)
    {
        string p = Normalize(prefix);
        var incoming = new Dictionary<string, StateValue>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(json))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch (JsonException) { incoming[p] = StateValue.Str(json); node = null; }
            if (node is not null) Collect(p, node, incoming);
        }

        // remove leaves under the prefix that the new payload no longer contains
        var doomed = new List<string>();
        foreach (string key in _values.Keys)
            if ((key == p || key.StartsWith(p + ".", StringComparison.Ordinal)) && !incoming.ContainsKey(key))
                doomed.Add(key);
        foreach (string key in doomed)
        {
            _values.Remove(key);
            Notify(key, StateValue.Null, removed: true);
        }

        // set the incoming leaves (Set de-dupes: unchanged values fire nothing)
        foreach (KeyValuePair<string, StateValue> kv in incoming)
            Set(kv.Key, kv.Value);
    }

    /// <summary>
    /// Watch a path or subtree. The callback fires with (changedPath, value) whenever
    /// <paramref name="path"/> or any descendant changes. Returns an <see cref="IDisposable"/>
    /// that unsubscribes.
    /// </summary>
    public IDisposable Watch(string path, Action<string, StateValue> onChange)
    {
        var w = new Watcher(Normalize(path), onChange);
        _watchers.Add(w);
        return new Subscription(this, w);
    }

    // ---- internals -----------------------------------------------------------

    private static void Collect(string prefix, JsonNode? node, Dictionary<string, StateValue> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                    Collect(prefix + "." + kv.Key.ToLowerInvariant(), kv.Value, into);
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                    Collect(prefix + "." + i.ToString(CultureInfo.InvariantCulture), arr[i], into);
                break;
            case JsonValue val:
                into[prefix] = FromJsonValue(val);
                break;
            case null:
                into[prefix] = StateValue.Null;
                break;
        }
    }

    private static StateValue FromJsonValue(JsonValue val)
    {
        if (val.TryGetValue(out bool b)) return StateValue.Boolean(b);
        if (val.TryGetValue(out double d)) return StateValue.Num(d);
        if (val.TryGetValue(out string? s)) return StateValue.Str(s);
        return StateValue.Str(val.ToString());
    }

    private void Notify(string key, StateValue value, bool removed)
    {
        Changed?.Invoke(new StateChange(key, value, removed));
        // Index-based: a watcher callback might add/remove watchers; snapshot the count.
        for (int i = 0; i < _watchers.Count; i++)
        {
            Watcher w = _watchers[i];
            if (Matches(w.Path, key)) w.Callback(key, value);
        }
    }

    private static bool Matches(string watchPath, string changedKey) =>
        watchPath.Length == 0
        || changedKey == watchPath
        || changedKey.StartsWith(watchPath + ".", StringComparison.Ordinal);

    private static string Normalize(string path) => (path ?? "").Trim().ToLowerInvariant();

    private sealed class Subscription : IDisposable
    {
        private readonly StateStore _store;
        private readonly Watcher _watcher;
        private bool _disposed;
        public Subscription(StateStore store, Watcher watcher) { _store = store; _watcher = watcher; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _store._watchers.Remove(_watcher);
        }
    }
}
