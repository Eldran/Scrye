using System.Text.Json;

namespace Scrye.Core.Plugins;

/// <summary>
/// Discovers plugins on disk. A plugin is any immediate subfolder of a root that
/// contains a <c>plugin.json</c>. Scrye scans a bundled folder (next to the exe) and
/// the user folder (<c>%APPDATA%/Scrye/plugins</c>); pass both roots. Pure filesystem +
/// JSON — no scripting dependency, so it is unit-testable without a Lua engine.
/// </summary>
public static class PluginCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Tidy a hand-typed plugin root. Blank (or a path that is nothing but quotes and spaces)
    /// comes back null, so callers can test one thing. Surrounding quotes are stripped —
    /// Explorer's "Copy as path" puts them there and pasting is how such a box gets filled.
    /// A folder that IS a plugin (it holds a <c>plugin.json</c> itself) is read as its parent:
    /// "the plugin folder" is the obvious thing to point at, and pointing there would otherwise
    /// scan a level too deep and silently find nothing.
    /// </summary>
    public static string? NormaliseRoot(string? path)
    {
        if (path is null) return null;
        string p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return null;
        try
        {
            if (File.Exists(Path.Combine(p, "plugin.json")))
            {
                string? parent = Path.GetDirectoryName(Path.GetFullPath(p.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                if (!string.IsNullOrEmpty(parent)) return parent;
            }
        }
        catch (ArgumentException) { }        // malformed path — hand it back as typed, so the
        catch (PathTooLongException) { }     // caller reports "folder not found" against what
        catch (NotSupportedException) { }    // the user actually wrote rather than a rewrite
        return p;
    }

    /// <summary>All valid plugins found under the given roots (missing roots skipped;
    /// a folder with no <c>plugin.json</c>, unparseable JSON, or no <c>id</c> is ignored).</summary>
    public static IReadOnlyList<PluginDescriptor> Discover(params string[] roots)
    {
        var result = new List<PluginDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (string dir in Directory.GetDirectories(root))
            {
                // a hidden folder is never a plugin: it is where the catalogue installer stages
                // and keeps the copy it is replacing, and those must not load
                if (Path.GetFileName(dir).StartsWith('.')) continue;
                string manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;

                PluginManifest? manifest;
                try { manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options); }
                catch (JsonException) { continue; }

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                if (!seen.Add(manifest.Id)) continue;   // first root wins on id collision

                result.Add(new PluginDescriptor(manifest, dir));
            }
        }
        return result;
    }

    /// <summary>
    /// Discovery for a world: <paramref name="overrideRoot"/> (the extra folder a plugin is being
    /// worked on in) wins outright on an id, as it always has. Among the other
    /// <paramref name="roots"/> (bundled, user) the NEWEST version of an id wins, and a tie goes
    /// to the earlier root. That is what lets a catalogue update of a bundled plugin, which
    /// lands in the user folder, actually load - and lets a later Scrye release whose bundled
    /// copy has overtaken that update take over again, without anyone deleting anything.
    /// </summary>
    public static IReadOnlyList<PluginDescriptor> DiscoverNewest(string? overrideRoot, params string[] roots)
    {
        var result = new List<PluginDescriptor>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(overrideRoot))
            foreach (PluginDescriptor d in Discover(overrideRoot))
            {
                index[d.Id] = result.Count;
                pinned.Add(d.Id);
                result.Add(d);
            }
        foreach (string root in roots)
            foreach (PluginDescriptor d in Discover(root))
            {
                if (pinned.Contains(d.Id)) continue;
                if (!index.TryGetValue(d.Id, out int at)) { index[d.Id] = result.Count; result.Add(d); }
                else if (PluginVersion.IsNewer(d.Manifest.Version, result[at].Manifest.Version)) result[at] = d;
            }
        return result;
    }

    /// <summary><see cref="AvailableForMud"/> over <see cref="DiscoverNewest"/>.</summary>
    public static IReadOnlyList<PluginDescriptor> AvailableForMudNewest(string mudId, string? overrideRoot, params string[] roots) =>
        DiscoverNewest(overrideRoot, roots).Where(d => d.AppliesTo(mudId)).ToList();

    /// <summary>Enabled plugins that apply to <paramref name="mudId"/>, discovered under the roots.</summary>
    public static IReadOnlyList<PluginDescriptor> ForMud(string mudId, params string[] roots) =>
        Discover(roots).Where(d => d.Manifest.Enabled && d.AppliesTo(mudId)).ToList();

    /// <summary>All plugins that apply to <paramref name="mudId"/> — the catalogue the plugin
    /// manager offers for opt-in. Ignores the manifest <c>Enabled</c> flag (which plugins load
    /// is decided per-character by the profile, not the manifest); only the MUD scope filters.</summary>
    public static IReadOnlyList<PluginDescriptor> AvailableForMud(string mudId, params string[] roots) =>
        Discover(roots).Where(d => d.AppliesTo(mudId)).ToList();
}
