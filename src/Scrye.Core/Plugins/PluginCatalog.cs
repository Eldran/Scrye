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

    /// <summary>Enabled plugins that apply to <paramref name="mudId"/>, discovered under the roots.</summary>
    public static IReadOnlyList<PluginDescriptor> ForMud(string mudId, params string[] roots) =>
        Discover(roots).Where(d => d.Manifest.Enabled && d.AppliesTo(mudId)).ToList();
}
