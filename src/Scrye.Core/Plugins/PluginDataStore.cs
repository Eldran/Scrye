using System.Text.Json;

namespace Scrye.Core.Plugins;

/// <summary>
/// Persistent per-plugin key/value storage — the backing for the <c>scrye.store</c>
/// script API. One JSON file per plugin, per world:
/// <c>&lt;baseDir&gt;/&lt;world&gt;/&lt;pluginId&gt;.json</c> (a flat string→string map), so a
/// mapper's rooms for one MUD never collide with another MUD's.
///
/// Values are strings, matching the rest of the <see cref="IPluginHost"/> surface.
/// Writes are write-through with an atomic replace (tmp + move), so a crash never
/// leaves a half-written file. A missing or corrupt file simply starts empty — a
/// broken store must never take the session down.
///
/// The in-memory copy of each file is PROCESS-WIDE (keyed by full file path), not per
/// instance: <c>scrye.shared</c> is scoped by MUD host, so two characters on the same MUD
/// connected at once each construct their own store over the SAME file. With a private
/// cache per instance each would write back its own stale map and silently drop the
/// other's data (the mapper's rooms). Sessions run on different threads, so every access
/// to a shared map is locked, and each save uses a unique temp file name.
/// </summary>
public sealed class PluginDataStore
{
    private readonly string _root;
    private readonly Action<string>? _report;
    // Entries this INSTANCE has already resolved and checked against disk (see Entry).
    private readonly Dictionary<string, Entry> _mine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One loaded file, shared by every store instance in the process that maps to it.
    /// <see cref="Stamp"/> is the file's (mtime, length) as last read or written by us: a NEW
    /// instance touching the file for the first time reloads it when disk no longer matches
    /// (edited or replaced outside this process), so a fresh store still sees the disk truth.
    /// </summary>
    private sealed class Entry
    {
        public Dictionary<string, string>? Map;
        public (DateTime, long) Stamp;
    }

    private static readonly Dictionary<string, Entry> Registry = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <param name="baseDir">Data root, e.g. <c>%APPDATA%/Scrye/plugin-data</c>.</param>
    /// <param name="worldName">The world this store is scoped to (sanitized into a folder name).</param>
    /// <param name="report">Optional sink for IO problems (shown in world output).</param>
    public PluginDataStore(string baseDir, string worldName, Action<string>? report = null)
    {
        _root = Path.Combine(baseDir, Sanitize(worldName));
        _report = report;
    }

    /// <summary>The stored value, or null if the key is unset.</summary>
    public string? Get(string pluginId, string key)
    {
        Entry e = Load(pluginId);
        lock (e) return e.Map!.TryGetValue(key, out string? v) ? v : null;
    }

    public void Set(string pluginId, string key, string value)
    {
        Entry e = Load(pluginId);
        lock (e)
        {
            Dictionary<string, string> map = e.Map!;
            if (map.TryGetValue(key, out string? existing) && existing == value) return;   // no-op write
            map[key] = value;
            Save(pluginId, e);
        }
    }

    /// <summary>
    /// Persist several key/value pairs with ONE file write (the <c>scrye.store.setMany</c>
    /// backing, plugin API 1.6). Per-key <see cref="Set"/> rewrites the plugin's whole JSON
    /// file each call — fine for a counter, quadratic for a mapper flushing an area's rooms.
    /// Unchanged values are skipped; if nothing actually changed, nothing is written.
    /// </summary>
    public void SetMany(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        Entry e = Load(pluginId);
        lock (e)
        {
            Dictionary<string, string> map = e.Map!;
            bool dirty = false;
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (map.TryGetValue(kv.Key, out string? existing) && existing == kv.Value) continue;
                map[kv.Key] = kv.Value;
                dirty = true;
            }
            if (dirty) Save(pluginId, e);
        }
    }

    /// <summary>Remove a key; true if it existed.</summary>
    public bool Delete(string pluginId, string key)
    {
        Entry e = Load(pluginId);
        lock (e)
        {
            if (!e.Map!.Remove(key)) return false;
            Save(pluginId, e);
            return true;
        }
    }

    /// <summary>All keys currently stored for the plugin (unordered).</summary>
    public string[] Keys(string pluginId)
    {
        Entry e = Load(pluginId);
        lock (e) return e.Map!.Keys.ToArray();
    }

    // ---- files ---------------------------------------------------------------

    /// <summary>The process-wide entry for the plugin's file, loaded (or re-validated) as needed.</summary>
    private Entry Load(string pluginId)
    {
        Entry? e;
        lock (_mine)
        {
            if (_mine.TryGetValue(pluginId, out e)) return e;   // hot path: no path building, no stat
        }
        string path = Path.GetFullPath(FileFor(pluginId));
        lock (Registry)
        {
            if (!Registry.TryGetValue(path, out e)) Registry[path] = e = new Entry();
        }
        lock (e)
        {
            // Loaded once per process; re-checked against disk once per new instance.
            if (e.Map is null || StampOf(path) != e.Stamp) Reload(pluginId, path, e);
        }
        lock (_mine) _mine[pluginId] = e;
        return e;
    }

    private void Reload(string pluginId, string path, Entry e)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded is not null) map = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _report?.Invoke($"plugin store for '{pluginId}' could not be read ({ex.Message}) — starting empty");
        }
        e.Map = map;
        e.Stamp = StampOf(path);
    }

    /// <summary>Write the entry's map to disk. Caller holds the entry's lock.</summary>
    private void Save(string pluginId, Entry e)
    {
        string path = FileFor(pluginId);
        try
        {
            Directory.CreateDirectory(_root);
            // Unique temp name: another process (a second Scrye) must not collide on one ".tmp".
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(e.Map, Options));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            e.Stamp = StampOf(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep the in-memory value; the next successful save persists it
            _report?.Invoke($"plugin store for '{pluginId}' could not be saved: {ex.Message}");
        }
    }

    private static (DateTime, long) StampOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    private string FileFor(string pluginId) => Path.Combine(_root, Sanitize(pluginId) + ".json");

    /// <summary>Make a name safe as a file/folder name (invalid chars → '_').</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_";
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }
}
