using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrye.Core.Plugins;

/// <summary>One file of a catalogued plugin: its path inside the plugin folder, and the
/// SHA-256 and size it must have when it arrives.</summary>
public sealed record CatalogFile
{
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>One plugin in the catalogue: enough of its manifest to list it and decide whether it
/// fits this build, plus where its files are and what they must hash to.</summary>
public sealed record CatalogEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string[] MudIds { get; init; } = { "*" };
    public string Lang { get; init; } = "lua";
    /// <summary>The manifest's <c>requires.scryeApi</c> range, or null.</summary>
    public string? Requires { get; init; }
    public string[] Permissions { get; init; } = Array.Empty<string>();
    /// <summary>The plugin's folder under the index's <see cref="CatalogIndex.Base"/>.</summary>
    public string Folder { get; init; } = "";
    public CatalogFile[] Files { get; init; } = Array.Empty<CatalogFile>();

    public bool AppliesTo(string mudId) =>
        MudIds.Length == 0 ||
        MudIds.Any(m => m == "*" || string.Equals(m, mudId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Can this build run it? Same rule, same wording as an installed plugin's check.</summary>
    public bool IsApiCompatible(out string reason) =>
        ScryeApi.IsCompatible(new PluginManifest { Id = Id, Requires = new PluginRequires { ScryeApi = Requires } },
                              out reason);

    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>Where a catalogued plugin stands against what is on disk.</summary>
public enum CatalogStatus
{
    /// <summary>Not on disk: Install.</summary>
    NotInstalled,
    /// <summary>The same version is on disk.</summary>
    UpToDate,
    /// <summary>The catalogue has a newer version: Update.</summary>
    UpdateAvailable,
    /// <summary>What is on disk is newer than the catalogue (a plugin being worked on).</summary>
    NewerInstalled,
    /// <summary>This build cannot run the catalogue's version.</summary>
    Incompatible,
}

/// <summary>
/// The plugin catalogue: a JSON index of plugins that can be installed from inside Scrye.
///
/// <para>It lives in the Scrye repo (<c>catalog/index.json</c> on main) and names a release
/// TAG: every file is fetched from that tag (<see cref="Base"/> + folder + path), so what a
/// user installs is exactly what was released, and the index can be updated on main without
/// moving anything already published. Each file carries its SHA-256 and size, which the
/// installer checks before anything is written into place - a truncated download or a file
/// changed behind the index installs nothing.</para>
///
/// <para>Built from git blobs rather than the working tree (see the catalogue builder), so a
/// Windows checkout with CRLF line endings hashes the same bytes GitHub serves.</para>
/// </summary>
public sealed record CatalogIndex
{
    /// <summary>The index Scrye reads unless told otherwise.</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/Eldran/Scrye/main/catalog/index.json";

    /// <summary>Where a tag's plugin folders are served from; <c>{ref}</c> is the tag.</summary>
    public const string DefaultBaseTemplate = "https://raw.githubusercontent.com/Eldran/Scrye/{ref}/src/Scrye.App/plugins/";

    /// <summary>The layout of this file. A reader refuses a format it does not know.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    /// <summary>The release the files come from (a tag, e.g. <c>v1.9.1</c>).</summary>
    public string Ref { get; init; } = "";
    /// <summary>URL the plugin folders sit under, ending in '/'.</summary>
    public string Base { get; init; } = "";
    public string? Generated { get; init; }
    public CatalogEntry[] Plugins { get; init; } = Array.Empty<CatalogEntry>();

    /// <summary>Most a single plugin may weigh; the installer refuses anything larger.</summary>
    public const long MaxPluginBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",                                          // LF on Windows too: it is committed
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep 'é' and '—' readable in the diff
    };

    /// <summary>The URL of one file of a plugin.</summary>
    public string FileUrl(CatalogEntry e, CatalogFile f)
    {
        string b = Base.EndsWith('/') ? Base : Base + "/";
        return b + Uri.EscapeDataString(e.Folder) + "/"
             + string.Join('/', f.Path.Split('/').Select(Uri.EscapeDataString));
    }

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions) + "\n";

    /// <summary>
    /// Read an index. Throws <see cref="InvalidDataException"/> with a sentence fit for the
    /// user when the file is not an index or is a format this build does not know. Entries
    /// that are unusable (no id, no files, a path that climbs out of its folder, a hash that is
    /// not a SHA-256) are dropped, not fatal: one bad entry must not hide the whole catalogue.
    /// </summary>
    public static CatalogIndex Parse(string json, Action<string>? report = null)
    {
        CatalogIndex? idx;
        try { idx = JsonSerializer.Deserialize<CatalogIndex>(json, ReadOptions); }
        catch (JsonException ex) { throw new InvalidDataException("the plugin catalogue is not valid JSON: " + ex.Message); }
        if (idx is null) throw new InvalidDataException("the plugin catalogue is empty");
        if (idx.Format != CurrentFormat)
            throw new InvalidDataException($"the plugin catalogue is format {idx.Format}; this Scrye reads format {CurrentFormat} - update Scrye");
        if (!Uri.TryCreate(idx.Base, UriKind.Absolute, out Uri? baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("the plugin catalogue has no https base address");

        var keep = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry? e in idx.Plugins ?? Array.Empty<CatalogEntry>())
        {
            if (e is null) continue;
            string? why = Problem(e);
            if (why is null && !seen.Add(e.Id)) why = "listed twice";
            if (why is not null) { report?.Invoke($"catalogue: skipped '{e.Id}' - {why}"); continue; }
            keep.Add(e with { MudIds = e.MudIds ?? new[] { "*" }, Permissions = e.Permissions ?? Array.Empty<string>() });
        }
        return idx with { Plugins = keep.ToArray() };
    }

    private static string? Problem(CatalogEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Id)) return "no id";
        if (!IsSafeRelative(e.Folder) || e.Folder.Contains('/')) return "bad folder name";
        if (e.Files is null || e.Files.Length == 0) return "no files";
        if (!e.Files.Any(f => f is not null && f.Path == "plugin.json")) return "no plugin.json";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogFile f in e.Files)
        {
            if (f is null || !IsSafeRelative(f.Path)) return "a file path leaves the plugin folder";
            if (!paths.Add(f.Path)) return $"'{f.Path}' listed twice";
            if (f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) return $"'{f.Path}' has no SHA-256";
            if (f.Size < 0) return $"'{f.Path}' has a negative size";
        }
        if (e.TotalSize > MaxPluginBytes) return "larger than a plugin may be";
        return null;
    }

    /// <summary>A path that stays inside the folder it is relative to: forward slashes, no
    /// empty, '.' or '..' segment, nothing rooted, no drive, no characters Windows refuses.</summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 260) return false;
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/')) return false;
        foreach (string seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
            if (seg.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0) return false;
            if (seg.Any(char.IsControl)) return false;
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>
    /// Build an index from a release's plugin files: <paramref name="files"/> are paths relative
    /// to the plugins folder (<c>3s-farmer/main.lua</c>) with their exact bytes. Every top-level
    /// folder holding a readable <c>plugin.json</c> with an id becomes one entry; test harnesses
    /// (<c>*_test.lua</c>) and dot-files stay out. Entries are sorted by id so the file diffs
    /// cleanly from release to release.
    /// </summary>
    public static CatalogIndex Build(IEnumerable<(string Path, byte[] Bytes)> files, string refName,
                                     string? baseTemplate = null, Action<string>? report = null)
    {
        var byFolder = new SortedDictionary<string, List<(string Rel, byte[] Bytes)>>(StringComparer.Ordinal);
        foreach ((string path, byte[] bytes) in files)
        {
            string p = path.Replace('\\', '/');
            int slash = p.IndexOf('/');
            if (slash <= 0) continue;                                  // loose file at the top
            string folder = p[..slash], rel = p[(slash + 1)..];
            if (!IncludeFile(rel)) continue;
            if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = new();
            list.Add((rel, bytes));
        }

        var entries = new List<CatalogEntry>();
        foreach ((string folder, var list) in byFolder)
        {
            (string _, byte[] manifestBytes) = list.FirstOrDefault(f => f.Rel == "plugin.json");
            if (manifestBytes is null) continue;
            PluginManifest? m;
            ReadOnlySpan<byte> utf8 = manifestBytes.AsSpan();
            if (utf8.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) utf8 = utf8[3..];   // a BOM is not JSON
            try { m = JsonSerializer.Deserialize<PluginManifest>(utf8, ReadOptions); }
            catch (JsonException ex) { report?.Invoke($"{folder}: plugin.json does not parse ({ex.Message}) - left out"); continue; }
            if (m is null || string.IsNullOrWhiteSpace(m.Id)) { report?.Invoke($"{folder}: plugin.json has no id - left out"); continue; }
            if (!IsSafeRelative(folder)) { report?.Invoke($"{folder}: folder name cannot be served - left out"); continue; }

            entries.Add(new CatalogEntry
            {
                Id = m.Id, Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, Version = m.Version,
                Author = m.Author, Description = m.Description, MudIds = m.MudIds, Lang = m.Lang,
                Requires = string.IsNullOrWhiteSpace(m.Requires?.ScryeApi) ? null : m.Requires!.ScryeApi,
                Permissions = m.Permissions, Folder = folder,
                Files = list.OrderBy(f => f.Rel, StringComparer.Ordinal)
                            .Select(f => new CatalogFile { Path = f.Rel, Sha256 = Sha256Hex(f.Bytes), Size = f.Bytes.LongLength })
                            .ToArray(),
            });
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new CatalogIndex
        {
            Ref = refName,
            Base = (baseTemplate ?? DefaultBaseTemplate).Replace("{ref}", refName, StringComparison.Ordinal),
            Generated = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Plugins = entries.ToArray(),
        };
    }

    /// <summary>Which files of a plugin folder ship: not the Lua test harness, not dot-files.</summary>
    public static bool IncludeFile(string rel)
    {
        if (rel.Split('/').Any(s => s.StartsWith('.'))) return false;
        string name = rel[(rel.LastIndexOf('/') + 1)..];
        return !name.EndsWith("_test.lua", StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Where <paramref name="entry"/> stands against the copy that would load now
    /// (<paramref name="installed"/>, or null when there is none).</summary>
    public static CatalogStatus StatusOf(CatalogEntry entry, PluginDescriptor? installed) =>
        StatusOfVersion(entry, installed?.Manifest.Version);

    /// <summary>The same, against the installed version alone (null = not installed).</summary>
    public static CatalogStatus StatusOfVersion(CatalogEntry entry, string? installedVersion)
    {
        if (installedVersion is null)
            return entry.IsApiCompatible(out _) ? CatalogStatus.NotInstalled : CatalogStatus.Incompatible;
        int c = PluginVersion.Compare(entry.Version, installedVersion);
        if (c == 0) return CatalogStatus.UpToDate;
        if (c < 0) return CatalogStatus.NewerInstalled;
        return entry.IsApiCompatible(out _) ? CatalogStatus.UpdateAvailable : CatalogStatus.Incompatible;
    }
}
