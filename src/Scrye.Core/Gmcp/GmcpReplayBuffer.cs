using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrye.Core.Gmcp;

/// <summary>
/// What a plugin loaded mid-session needs to catch up: for each GMCP package, the messages
/// that rebuild its current picture, in the order they arrived.
///
/// <para>It exists because most of the 3Scapes feed is sent once. Guild.Kingdom, Guild.Roster,
/// Merc.Info and the rest arrive as a full report at login and as changes after that, so a
/// plugin switched on (or reloaded, or updated from the catalogue) an hour into the session
/// heard none of the report and sat on "waiting for Guild.Kingdom..." until the next login.
/// The plugin manager replays this to a plugin the moment it loads, through the same
/// <c>onGmcp</c> hooks it uses for live messages, so it cannot tell a replay from a login.</para>
///
/// <para>Raw messages, not a merged state: every plugin assembles its packages its own way
/// (the Viking plugins stitch paged bursts, Merc merges deltas into a snapshot), and the only
/// replay that suits all of them is the messages they would have seen. What is kept depends on
/// how the package speaks, the same three shapes <see cref="State.StateStore.SetJson"/> knows:</para>
/// <list type="bullet">
/// <item>whole - every message replaces the last: keep the last one;</item>
/// <item>snapshot/delta (<c>"full": 1</c>, then changes) - keep the last full one and the
/// changes after it;</item>
/// <item>paged (<c>"page"</c>/<c>"pages"</c>) - keep from the last complete burst that carried
/// <c>full</c> (or everything, if none did), plus any burst still arriving.</item>
/// </list>
/// <para>Events are not state and are never replayed: a chat line (<c>Comm.*</c>) or a kill
/// (<c>Room.Death</c>) heard twice would be shown, logged or beeped twice. Each package is
/// bounded; past the bound the oldest change is folded into the snapshot before it (or, for a
/// paged package, the oldest burst is dropped), so memory stays small however long the
/// session runs.</para>
/// </summary>
public sealed class GmcpReplayBuffer
{
    /// <summary>Most messages kept for one package.</summary>
    public const int MaxMessagesPerPackage = 256;

    /// <summary>Most characters kept for one package.</summary>
    public const int MaxCharsPerPackage = 2 * 1024 * 1024;

    private sealed class Entry
    {
        public required long Seq;
        public required string Json;
        public bool StartsBurst;
    }

    private sealed class Log
    {
        public readonly List<Entry> Items = new();
        public bool Merges;          // snapshot/delta or paged: a message adds to the ones before
        public bool Paged;
        public int BurstStart = -1;  // index in Items of the burst still arriving, or -1
        public int LastPage;
        public bool BurstFull;
        public long Chars;
    }

    private readonly Dictionary<string, (string Name, Log Log)> _logs = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;

    /// <summary>Packages that are events rather than state: never replayed.</summary>
    public static bool IsEvent(string package) =>
        package.StartsWith("Comm.", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Core.", StringComparison.OrdinalIgnoreCase)
        || package.Equals("Room.Death", StringComparison.OrdinalIgnoreCase);

    /// <summary>A new connection: everything the last one sent is gone (the login resends it).</summary>
    public void Clear()
    {
        _logs.Clear();
        _seq = 0;
    }

    public int PackageCount => _logs.Count;

    /// <summary>Remember one message as it arrives.</summary>
    public void Record(string package, string json)
    {
        if (string.IsNullOrEmpty(package) || IsEvent(package)) return;
        json ??= "";
        if (!_logs.TryGetValue(package, out (string Name, Log Log) slot))
        {
            slot = (package, new Log());
            _logs[package] = slot;
        }
        Log log = slot.Log;
        var entry = new Entry { Seq = ++_seq, Json = json };
        (int page, int pages, bool full) = Shape(json);

        if (pages > 0)
        {
            log.Merges = log.Paged = true;
            if (log.BurstStart < 0 || page <= log.LastPage)
            {
                log.BurstStart = log.Items.Count;      // a new burst begins with this page
                log.BurstFull = false;
                entry.StartsBurst = true;
            }
            log.LastPage = page;
            log.BurstFull |= full;
            Add(log, entry);
            if (page >= pages)
            {
                // A complete burst that says it is the whole report replaces everything before it.
                if (log.BurstFull && log.BurstStart > 0) RemoveFront(log, log.BurstStart);
                log.BurstStart = -1;
                log.LastPage = 0;
                log.BurstFull = false;
            }
        }
        else if (full && !log.Paged)
        {
            log.Merges = true;
            RemoveFront(log, log.Items.Count);
            log.BurstStart = -1;
            Add(log, entry);
        }
        else if (log.Merges)
        {
            Add(log, entry);                            // a change, or a partial of a paged package
        }
        else
        {
            RemoveFront(log, log.Items.Count);          // whole: this one is the picture
            Add(log, entry);
        }
        Trim(log);
    }

    /// <summary>Every kept message, oldest first across all packages.</summary>
    public IReadOnlyList<(string Package, string Json)> Snapshot()
    {
        var all = new List<(long Seq, string Package, string Json)>();
        foreach ((string name, Log log) in _logs.Values)
            foreach (Entry e in log.Items) all.Add((e.Seq, name, e.Json));
        all.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var outp = new List<(string, string)>(all.Count);
        foreach ((_, string p, string j) in all) outp.Add((p, j));
        return outp;
    }

    private static void Add(Log log, Entry e)
    {
        log.Items.Add(e);
        log.Chars += e.Json.Length;
    }

    private static void RemoveFront(Log log, int count)
    {
        for (int i = 0; i < count; i++) log.Chars -= log.Items[i].Json.Length;
        log.Items.RemoveRange(0, count);
        if (log.BurstStart >= 0) log.BurstStart = Math.Max(0, log.BurstStart - count);
    }

    private static void Trim(Log log)
    {
        while (log.Items.Count > 1 && (log.Items.Count > MaxMessagesPerPackage || log.Chars > MaxCharsPerPackage))
        {
            if (log.Paged)
            {
                // drop the oldest burst whole: half a burst would replay as a different report
                int next = 1;
                while (next < log.Items.Count && !log.Items[next].StartsBurst) next++;
                if (next >= log.Items.Count) next = 1;  // one burst bigger than the bound: drop its oldest page
                RemoveFront(log, next);
            }
            else
            {
                // fold the oldest change into the snapshot before it
                Entry a = log.Items[0], b = log.Items[1];
                string merged = Merge(a.Json, b.Json);
                log.Chars += merged.Length - a.Json.Length - b.Json.Length;
                log.Items[1] = new Entry { Seq = b.Seq, Json = merged };
                log.Items.RemoveAt(0);
            }
        }
    }

    /// <summary>A delta applied to the message before it: objects merge key by key, anything
    /// else (a number, a string, a list) is replaced. What is not JSON is simply the later one.</summary>
    public static string Merge(string earlier, string later)
    {
        try
        {
            if (JsonNode.Parse(earlier) is JsonObject a && JsonNode.Parse(later) is JsonObject b)
            {
                MergeInto(a, b);
                return a.ToJsonString();
            }
        }
        catch (JsonException) { }
        return later;
    }

    private static void MergeInto(JsonObject into, JsonObject from)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in from.ToList())
        {
            if (into[kv.Key] is JsonObject io && kv.Value is JsonObject fo) MergeInto(io, fo);
            else into[kv.Key] = kv.Value?.DeepClone();
        }
    }

    // The top-level page / pages / full of a payload. Cheap when they are absent, which is
    // most messages: the text is searched before anything is parsed.
    private static (int Page, int Pages, bool Full) Shape(string json)
    {
        if (!json.Contains("\"pages\"", StringComparison.Ordinal) && !json.Contains("\"full\"", StringComparison.Ordinal))
            return (0, 0, false);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (0, 0, false);
            int page = Int(doc.RootElement, "page"), pages = Int(doc.RootElement, "pages");
            bool full = Int(doc.RootElement, "full") != 0
                        || (doc.RootElement.TryGetProperty("full", out JsonElement f) && f.ValueKind == JsonValueKind.True);
            return (pages > 0 ? Math.Max(page, 1) : 0, pages, full);
        }
        catch (JsonException) { return (0, 0, false); }
    }

    private static int Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
        return 0;
    }
}
