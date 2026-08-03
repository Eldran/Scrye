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
        _subs[sub.Id] = sub;
        Save();
    }

    /// <summary>Forget a subscription — on explicit opt-out, or when the push service says
    /// it is gone.</summary>
    public bool Remove(string id)
    {
        bool removed = _subs.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    public void Clear()
    {
        _subs.Clear();
        Save();
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

    private void Save()
    {
        if (_path is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_subs.Values.ToArray()));
        }
        catch (Exception) { /* unwritable: keep working in memory for this run */ }
    }
}
