using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Scrye.Core.Plugins;

/// <summary>
/// Install/uninstall support for the <c>.scryeplugin</c> package format — a plain zip of a
/// plugin folder (a <c>plugin.json</c> plus its entry script and assets). Pure filesystem +
/// System.IO.Compression, no scripting dependency, so it is unit-testable. Installing extracts
/// the package into <c>&lt;userRoot&gt;/&lt;id&gt;/</c>, where the normal <see cref="PluginCatalog"/>
/// discovery then picks it up.
/// </summary>
public static class PluginPackage
{
    public const string Extension = ".scryeplugin";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Extract a package into <c>&lt;userRoot&gt;/&lt;id&gt;/</c> and return the plugin id.
    /// Accepts a zip with <c>plugin.json</c> at the root or inside a single top-level folder.</summary>
    public static string Install(string packagePath, string userRoot)
    {
        using ZipArchive zip = ZipFile.OpenRead(packagePath);

        // the shallowest plugin.json defines the plugin's root within the archive
        ZipArchiveEntry? manifestEntry = zip.Entries
            .Where(e => string.Equals(e.Name, "plugin.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();
        if (manifestEntry is null)
            throw new InvalidDataException("package contains no plugin.json");

        string prefix = manifestEntry.FullName[..^manifestEntry.Name.Length];   // "" or "folder/"

        string json;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            json = reader.ReadToEnd();
        PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(json, ManifestOptions);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidDataException("package manifest is missing an 'id'");

        string dest = Path.Combine(userRoot, Sanitize(manifest.Id));
        string destFull = Path.GetFullPath(dest);
        Directory.CreateDirectory(dest);

        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;                 // directory entry
            if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = e.FullName[prefix.Length..];
            if (rel.Length == 0) continue;

            string target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
        return manifest.Id;
    }

    /// <summary>Install every <c>*.scryeplugin</c> sitting in <paramref name="userRoot"/>, deleting each
    /// archive after a successful install. Non-throwing per file (failures go to <paramref name="report"/>).
    /// Returns the installed ids. This is what the manager's "drop a package here + rescan" flow uses.</summary>
    public static IReadOnlyList<string> InstallAllIn(string userRoot, Action<string>? report = null)
    {
        var installed = new List<string>();
        if (string.IsNullOrEmpty(userRoot) || !Directory.Exists(userRoot)) return installed;

        foreach (string pkg in Directory.GetFiles(userRoot, "*" + Extension))
        {
            try
            {
                string id = Install(pkg, userRoot);
                installed.Add(id);
                report?.Invoke($"installed plugin '{id}' from {Path.GetFileName(pkg)}");
                try { File.Delete(pkg); } catch { /* leave the archive if it can't be removed */ }
            }
            catch (Exception ex)
            {
                report?.Invoke($"could not install {Path.GetFileName(pkg)}: {ex.Message}");
            }
        }
        return installed;
    }

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        string s = sb.ToString().Trim('.', '_');
        return s.Length == 0 ? "plugin" : s;
    }
}
