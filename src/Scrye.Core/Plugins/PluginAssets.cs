using System.Text.Json;

namespace Scrye.Core.Plugins;

/// <summary>
/// Loads the data files a plugin declares in its manifest's <c>data</c> map and hands the
/// scripting runtime a plain object graph to publish as <c>scrye.data</c>.
///
/// <para><b>Why declarative.</b> Plugins have no filesystem, and the alternative to this was a
/// <c>readFile(path)</c> the host would have to defend: '..', absolute paths, Windows device
/// names, alternate data streams. Here the plugin never names a path at load time — it names
/// files once, in its manifest, and the host resolves them. There is no traversal to defend
/// against because there is no caller-supplied path.</para>
///
/// <para><b>What it is not.</b> Read-only, and only from the plugin's own folder. Nothing here
/// writes. Persisting plugin state is still <c>scrye.store</c>'s job — these files are the
/// plugin's <i>source</i>: a word list, a route table, a map, a colour palette. The kind of
/// thing an author edits in their repo and ships with the plugin.</para>
///
/// <para><b>It never throws.</b> A missing file, unreadable JSON, an over-long name or an
/// oversized file drops that one entry and reports it. A plugin whose data failed to load sees
/// nil at that key and can say so; it does not fail to start. One bad asset must never cost the
/// user a working plugin.</para>
/// </summary>
public static class PluginAssets
{
    /// <summary>Largest single data file that will be read. Generous for text — the route table
    /// this was built for is 26 KB — but bounded, because the file is read fully into memory on
    /// the session thread at load.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Most entries honoured from one manifest. A cap only a runaway generator would
    /// reach; it exists so a malformed manifest cannot stall startup.</summary>
    public const int MaxEntries = 32;

    /// <summary>Longest accepted file name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Read every declared data file under <paramref name="folderPath"/>.
    /// </summary>
    /// <param name="folderPath">The plugin's own folder. Nothing outside it is reachable.</param>
    /// <param name="declared">The manifest's <c>data</c> map: script key → file name. Null or
    /// empty yields an empty result, which is the overwhelmingly common case.</param>
    /// <param name="report">Optional sink for problems, phrased for the user. Each message names
    /// the key so an author can tell which entry misbehaved.</param>
    /// <returns>Key → parsed value. Keys whose file failed to load are absent, not null-valued,
    /// so a script can tell "declared but broken" from "parsed to nothing".</returns>
    public static IReadOnlyDictionary<string, object?> Load(
        string folderPath,
        IReadOnlyDictionary<string, string>? declared,
        Action<string>? report = null)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (declared is null || declared.Count == 0) return result;

        int taken = 0;
        foreach (KeyValuePair<string, string> entry in declared)
        {
            if (taken >= MaxEntries)
            {
                report?.Invoke($"data: more than {MaxEntries} entries declared; '{entry.Key}' and the rest were ignored");
                break;
            }
            taken++;

            if (!IsSafeKey(entry.Key))
            {
                report?.Invoke($"data: '{entry.Key}' is not a usable name (letters, digits and _ only, not starting with a digit)");
                continue;
            }
            if (!IsSafeFileName(entry.Value))
            {
                report?.Invoke($"data: '{entry.Key}' names an unsafe file '{entry.Value}' — plain file names only, no folders");
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(folderPath, entry.Value));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                report?.Invoke($"data: '{entry.Key}' — bad path: {ex.Message}");
                continue;
            }

            // Defence in depth. IsSafeFileName already rules out separators, so this can only
            // fire if that check is ever loosened — which is exactly when it should still hold.
            if (!IsInside(folderPath, full))
            {
                report?.Invoke($"data: '{entry.Key}' resolves outside the plugin folder — ignored");
                continue;
            }

            string text;
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists)
                {
                    report?.Invoke($"data: '{entry.Key}' — no file named '{entry.Value}' in the plugin folder");
                    continue;
                }
                if (info.Length > MaxFileBytes)
                {
                    report?.Invoke($"data: '{entry.Key}' — '{entry.Value}' is {(info.Length + 1023) / 1024} KB, over the {MaxFileBytes / 1024} KB limit");
                    continue;
                }
                text = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report?.Invoke($"data: '{entry.Key}' — could not read '{entry.Value}': {ex.Message}");
                continue;
            }

            try
            {
                result[entry.Key] = Parse(entry.Value, text);
            }
            catch (JsonException ex)
            {
                report?.Invoke($"data: '{entry.Key}' — '{entry.Value}' is not valid JSON: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Turn a file's text into a value, choosing by extension so the manifest stays declarative
    /// and the script never has to parse anything itself:
    /// <list type="bullet">
    /// <item><c>.json</c> — object graph: dictionary, list, string, number, bool or null.</item>
    /// <item><c>.txt</c> / <c>.list</c> / <c>.lines</c> / <c>.words</c> — a list of the non-blank
    /// lines, trimmed, with <c>#</c> comment lines dropped. This is the word-list case, and
    /// making it free is most of the point.</item>
    /// <item>anything else — the raw text, for a template or a format the plugin owns.</item>
    /// </list>
    /// </summary>
    public static object? Parse(string fileName, string text)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".json" => FromJson(text),
            ".txt" or ".list" or ".lines" or ".words" => Lines(text),
            _ => text,
        };
    }

    /// <summary>A key usable as a script identifier, so <c>scrye.data.areas</c> works with plain
    /// dot access rather than forcing bracket syntax on every author.</summary>
    public static bool IsSafeKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxNameLength) return false;
        if (!(char.IsAsciiLetter(key[0]) || key[0] == '_')) return false;
        foreach (char c in key)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>
    /// A plain file name in the plugin folder — no directory part, no traversal, no device.
    /// Deliberately strict: this is the only thing standing between a manifest and the disk,
    /// so it allowlists rather than blocklists.
    /// </summary>
    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name[0] == '.' || name[^1] == '.') return false;

        if (!(char.IsAsciiLetterOrDigit(name[0]) || name[0] == '_')) return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;

        // Windows still resolves these to devices inside any directory, so a file named
        // "CON.json" would open the console rather than a file. Rejected on every platform,
        // because a plugin folder is meant to be portable between them.
        string stem = name;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        if (Reserved.Contains(stem)) return false;

        return true;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsInside(string folder, string candidate)
    {
        string root = Path.GetFullPath(folder);
        if (root.Length == 0) return false;
        if (root[^1] != Path.DirectorySeparatorChar) root += Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static List<object?> Lines(string text)
    {
        var list = new List<object?>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;
            list.Add(line);
        }
        return list;
    }

    private static object? FromJson(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return FromElement(doc.RootElement);
    }

    private static object? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty p in e.EnumerateObject()) map[p.Name] = FromElement(p.Value);
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in e.EnumerateArray()) list.Add(FromElement(item));
                return list;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                // one numeric type, because the scripting side is happiest with one: Lua and
                // where every number is a double, and Jint's is a JS number
                return e.GetDouble();
            case JsonValueKind.True:  return true;
            case JsonValueKind.False: return false;
            default:                  return null;
        }
    }
}
