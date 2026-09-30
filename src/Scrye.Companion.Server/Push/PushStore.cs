using System.Collections.Concurrent;
using System.Text.Json;

namespace Scrye.Companion.Server.Push;

/// <summary>
/// The set of devices that asked to be notified, persisted to disk.
///
/// <para>Persistence matters for the same reason the VAPID keypair's does: a subscription is
/// created once, when the user taps "enable notifications", and is expected to keep working
/// across desktop restarts. Losing the file means silently never notifying again.</para>
///
/// <para>Thread-safe because subscriptions arrive on Kestrel threads while sends are kicked
/// off from the session loop.</para>
/// </summary>
public sealed class PushStore
{
    private readonly ConcurrentDictionary<string, PushSubscription> _subs = new(StringComparer.Ordinal);
    private readonly string? _path;

    // Serialises mutate+save. Adds arrive on Kestrel threads and removals (expired
    // subscriptions) from the notifier, so two File.WriteAllText calls could otherwise race
    // on the same file and one of them throw or leave a torn write.
    private readonly object _lock = new();

    /// <summary>Most devices this desktop will remember. The subscribe frame is
    /// client-supplied; without a cap one device could grow the file (and every
    /// notification's fan-out) without limit. Generous for one person's phones and tablets.</summary>
    public const int MaxSubscriptions = 20;

    public PushStore(string? path = null)
    {
        _path = path;
        Load();
    }

    public int Count => _subs.Count;

    public IReadOnlyCollection<PushSubscription> All => _subs.Values.ToArray();

    /// <summary>Add or replace. Re-subscribing with the same endpoint updates the keys
    /// rather than accumulating duplicates, which is what a browser does after a permission
    /// reset.</summary>
    public void Add(PushSubscription sub)
    {
        lock (_lock)
        {
            _subs[sub.Id] = sub;
            Save();
        }
    }

    /// <summary><see cref="Add"/>, unless the store is full and this is a new endpoint.
    /// Re-subscribing an endpoint already stored always succeeds.</summary>
    public bool TryAdd(PushSubscription sub)
    {
        lock (_lock)
        {
            if (!_subs.ContainsKey(sub.Id) && _subs.Count >= MaxSubscriptions) return false;
            _subs[sub.Id] = sub;
            Save();
            return true;
        }
    }

    /// <summary>Forget a subscription — on explicit opt-out, or when the push service says
    /// it is gone.</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            bool removed = _subs.TryRemove(id, out _);
            if (removed) Save();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _subs.Clear();
            Save();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            PushSubscription[]? loaded =
                JsonSerializer.Deserialize<PushSubscription[]>(File.ReadAllText(_path));
            foreach (PushSubscription s in loaded ?? Array.Empty<PushSubscription>())
                if (!string.IsNullOrEmpty(s.Endpoint)) _subs[s.Id] = s;
        }
        catch (Exception) { /* corrupt file: start empty rather than refusing to run */ }
    }

    /// <summary>Callers hold <see cref="_lock"/>.</summary>
    private void Save()
    {
        if (_path is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write a sibling temp file and swap it in, so a crash mid-write leaves the old
            // list intact instead of a truncated file that Load would discard wholesale.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_subs.Values.ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { /* unwritable: keep working in memory for this run */ }
    }
}
