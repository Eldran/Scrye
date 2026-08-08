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
/// broken store must never take the session down. All calls are expected on the
/// session loop thread (like every other host call), so there is no locking.
/// </summary>
public sealed class PluginDataStore
{
    private readonly string _root;
    private readonly Action<string>? _report;
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

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
    public string? Get(string pluginId, string key) =>
        Map(pluginId).TryGetValue(key, out string? v) ? v : null;

    public void Set(string pluginId, string key, string value)
    {
        Dictionary<string, string> map = Map(pluginId);
        if (map.TryGetValue(key, out string? existing) && existing == value) return;   // no-op write
        map[key] = value;
        Save(pluginId, map);
    }

    /// <summary>
    /// Persist several key/value pairs with ONE file write (the <c>scrye.store.setMany</c>
    /// backing, plugin API 1.6). Per-key <see cref="Set"/> rewrites the plugin's whole JSON
    /// file each call — fine for a counter, quadratic for a mapper flushing an area's rooms.
    /// Unchanged values are skipped; if nothing actually changed, nothing is written.
    /// </summary>
    public void SetMany(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        Dictionary<string, string> map = Map(pluginId);
        bool dirty = false;
        foreach (KeyValuePair<string, string> kv in values)
        {
            if (map.TryGetValue(kv.Key, out string? existing) && existing == kv.Value) continue;
            map[kv.Key] = kv.Value;
            dirty = true;
        }
        if (dirty) Save(pluginId, map);
    }

    /// <summary>Remove a key; true if it existed.</summary>
    public bool Delete(string pluginId, string key)
    {
        Dictionary<string, string> map = Map(pluginId);
        if (!map.Remove(key)) return false;
        Save(pluginId, map);
        return true;
    }

    /// <summary>All keys currently stored for the plugin (unordered).</summary>
    public string[] Keys(string pluginId) => Map(pluginId).Keys.ToArray();

    // ---- files ---------------------------------------------------------------

    private Dictionary<string, string> Map(string pluginId)
    {
        if (_cache.TryGetValue(pluginId, out Dictionary<string, string>? map)) return map;

        map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = FileFor(pluginId);
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
        _cache[pluginId] = map;
        return map;
    }

    private void Save(string pluginId, Dictionary<string, string> map)
    {
        string path = FileFor(pluginId);
        try
        {
            Directory.CreateDirectory(_root);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep the in-memory value; the next successful save persists it
            _report?.Invoke($"plugin store for '{pluginId}' could not be saved: {ex.Message}");
        }
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
