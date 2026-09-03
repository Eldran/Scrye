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

    /// <summary>Packages that have been seen to arrive PAGED, and so are never pruned again.
    /// See <see cref="SetJson"/> for why. Keyed by normalised prefix.</summary>
    private readonly HashSet<string> _paged = new(StringComparer.Ordinal);

    /// <summary>Packages that have been seen to carry a <c>full</c> flag, and so speak in
    /// snapshots and deltas. See <see cref="SetJson"/>. Keyed by normalised prefix.</summary>
    private readonly HashSet<string> _snapshotDelta = new(StringComparer.Ordinal);

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
    /// Used to drop a character's state on relogin, and as the way to force a fresh start for a
    /// paged package, whose tree <see cref="SetJson"/> deliberately never prunes.</summary>
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
    ///
    /// <para><b>Paged packages are never pruned.</b> "A resend replaces the whole object" is
    /// true of Char.Vitals, Char.Combat and Room.Info — and false of every Guild.* package on
    /// 3Scapes, which split one logical report across several messages carrying
    /// <c>{"page": 2, "pages": 3}</c>. Pruning on those made each page delete the one before
    /// it: Guild.State carries <c>bars</c> on page 1 and <c>points</c> on page 3, so
    /// <c>guild.state.points.viga</c> existed only in the gap between page 3 landing and the
    /// next page 1 — which is precisely why a Viking's Seid/Vig/Rad gauges blinked to zero
    /// while HP, which comes from unpaged Char.Vitals, sat still.</para>
    ///
    /// <para>So the first payload carrying a <c>pages</c> field latches its package as paged,
    /// and from then on that package only ever merges. Pruning survives untouched where it is
    /// actually needed: no never-paged package is affected, including the empty Char.Combat
    /// snapshot that clearing exists for. The latch is per package and deliberately sticky —
    /// Guild.State also sends UNPAGED partial payloads, and pruning on one of those would wipe
    /// the paged keys just as surely.</para>
    ///
    /// <para>The cost is that a paged package's tree never forgets: a leaf the server stops
    /// sending keeps its last value, and a list that shrinks leaves its tail behind (three
    /// carts becoming one leaves <c>…carts.2.*</c> in place). A consumer that must know the
    /// current extent of a list should assemble the burst itself from the raw JSON — the
    /// Viking plugins do exactly that — or call <see cref="ClearPrefix"/> first.</para>
    ///
    /// <para><b>Snapshot/delta packages prune only on a snapshot.</b> The third shape 3Scapes
    /// speaks (seen the day Merc and Mud were first subscribed, 2 Sep 2026): the first payload
    /// carries <c>"full": 1</c> and every field, and every later one carries only what changed
    /// and no <c>full</c> at all — Merc.Vitals sends <c>{hp, hp_max, stam, ap, …}</c> once and
    /// then <c>{stam, target_hp}</c> per round; Merc.Stats, Merc.Info and Mud.Status do the
    /// same. Whole-object pruning on the first delta deleted <c>merc.vitals.hp</c> and every
    /// other field the round did not touch, which is the Seid-gauge blink all over again on
    /// an unpaged package. So the first payload carrying <c>full</c> latches its package as
    /// snapshot/delta, and from then on <c>full</c> present means "replace the tree" and
    /// absent means "merge". Room.Contents sends <c>full</c> on every payload, so an empty
    /// room still clears the previous room's items exactly as before; a paged package stays
    /// never-pruned whatever its <c>full</c> says, since the page latch is the stronger
    /// claim. <see cref="MergeModeOf"/> reports which latch a package holds, for the audit.</para>
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

        // a "pages" field means this package speaks in bursts, not whole objects
        if (incoming.ContainsKey(p + ".pages")) _paged.Add(p);
        // a "full" field means this package speaks in snapshots and deltas
        bool full = incoming.ContainsKey(p + ".full");
        if (full) _snapshotDelta.Add(p);

        // remove leaves under the prefix that the new payload no longer contains — unless the
        // package is paged ("not in this payload" means "on another page"), or it is a
        // snapshot/delta package and this payload is a delta ("not in this payload" means
        // "unchanged")
        bool prune = !_paged.Contains(p) && (!_snapshotDelta.Contains(p) || full);
        if (prune)
        {
            var doomed = new List<string>();
            foreach (string key in _values.Keys)
                if ((key == p || key.StartsWith(p + ".", StringComparison.Ordinal)) && !incoming.ContainsKey(key))
                    doomed.Add(key);
            foreach (string key in doomed)
            {
                _values.Remove(key);
                Notify(key, StateValue.Null, removed: true);
            }
        }

        // set the incoming leaves (Set de-dupes: unchanged values fire nothing)
        foreach (KeyValuePair<string, StateValue> kv in incoming)
            Set(kv.Key, kv.Value);
    }

    /// <summary>How <see cref="SetJson"/> treats a package, from what it has sent so far:
    /// <c>"paged"</c> (never pruned), <c>"snapshot/delta"</c> (pruned only when the payload
    /// carries <c>full</c>), or <c>"whole"</c> (every payload replaces the tree). For the
    /// GMCP audit, so a plugin author can see which rule their package falls under.</summary>
    public string MergeModeOf(string prefix)
    {
        string p = Normalize(prefix);
        if (_paged.Contains(p)) return "paged";
        if (_snapshotDelta.Contains(p)) return "snapshot/delta";
        return "whole";
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
