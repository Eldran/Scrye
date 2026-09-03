using System.Text;
using System.Text.Json;

namespace Scrye.Core.Gmcp;

/// <summary>What has been seen of one GMCP package.</summary>
/// <param name="Package">The package name exactly as the server spelled it.</param>
/// <param name="Count">How many messages have arrived for it.</param>
/// <param name="Last">The most recent payload, verbatim.</param>
/// <param name="LastAt">When that arrived.</param>
public sealed record GmcpPackageSeen(string Package, int Count, string Last, DateTime LastAt);

/// <summary>
/// The GMCP side of <c>.gmcp</c>: what we asked for, what the server said it would send, and
/// what has actually turned up.
///
/// <para>It exists because the three are routinely different and only one of them is visible
/// without help. A package can be advertised in <c>Core.Supported</c> and never sent because
/// nothing about it has changed; it can arrive under a name half a case away from the one a
/// plugin filters on; and the whole feed can be silent because a subscription never landed.
/// Each of those looks identical from the output pane — nothing happens — and each needs a
/// different fix, so the tool that tells them apart earns its keep on the first evening of a
/// protocol going live.</para>
///
/// <para>Cheap enough to leave running: a dictionary write per message, on the session loop.
/// Payloads are kept one-deep per package rather than as a log, because the question being
/// asked is "what does this actually look like", not "what happened".</para>
/// </summary>
public sealed class GmcpAudit
{
    private sealed class Entry
    {
        public int Count;
        public string Last = "";
        public DateTime LastAt;

        // Every DIFFERENT payload, in the order first seen. Keeping only the last one answered
        // "what does this package look like" and nothing else -- 534 Room.Info messages
        // collapsed to whichever room you happened to be standing in when you ran the report,
        // so a walk through three areas showed one. What the feed looked like is a different
        // question from what the feed DID, and the second is the one worth capturing.
        public readonly List<string> Distinct = new();
        public readonly HashSet<string> DistinctSeen = new(StringComparer.Ordinal);
        public bool Truncated;
    }

    /// <summary>Distinct payloads kept per package. Two hundred is far more rooms than a
    /// session walks through and small enough to be free; when it is reached the report says
    /// so rather than quietly showing a partial picture.</summary>
    private const int MaxDistinct = 200;

    // Ordinal, not OrdinalIgnoreCase: two spellings of a package name is exactly the kind of
    // thing this is here to make visible, so they must not be silently folded together.
    private readonly Dictionary<string, Entry> _seen = new(StringComparer.Ordinal);

    /// <summary>Whether the telnet option was negotiated at all.</summary>
    public bool Negotiated { get; set; }

    /// <summary>What we sent as the subscription, for the report. Null until the handshake runs.</summary>
    public string? SubscriptionSent { get; set; }

    /// <summary>The verb the subscription went out under — <c>Core.Supports.Set</c>, or the
    /// bare <c>Core.Supports</c> fallback if the first one drew no answer.</summary>
    public string? SubscriptionVerb { get; set; }

    /// <summary>The server's <c>Core.Supported</c> payload, or null if it never answered.</summary>
    public string? Supported { get; private set; }

    /// <summary>Echo every package into the output as it arrives (<c>.gmcp raw on</c>).</summary>
    public bool Raw { get; set; }

    public int PackagesSeen => _seen.Count;
    public int MessagesSeen { get; private set; }

    /// <summary>True once anything at all has arrived — the one fact that says the subscription
    /// worked, as opposed to the option merely having been negotiated.</summary>
    public bool AnyData => MessagesSeen > 0;

    public void Observe(string package, string json)
    {
        MessagesSeen++;
        if (string.Equals(package, "Core.Supported", StringComparison.OrdinalIgnoreCase))
            Supported = json;

        if (!_seen.TryGetValue(package, out Entry? e)) _seen[package] = e = new Entry();
        e.Count++;
        e.Last = json;
        e.LastAt = DateTime.Now;

        if (e.DistinctSeen.Add(json))
        {
            if (e.Distinct.Count < MaxDistinct) e.Distinct.Add(json);
            else e.Truncated = true;
        }
    }

    /// <summary>Every different payload seen for a package, in the order they first arrived.</summary>
    public IReadOnlyList<string> Distinct(string package) =>
        Find2(package) is { } e ? e.Distinct : Array.Empty<string>();

    /// <summary>How many different payloads a package has sent — the count of rooms walked
    /// through, as against the count of times the room was announced.</summary>
    public int DistinctCount(string package) => Find2(package)?.DistinctSeen.Count ?? 0;

    private Entry? Find2(string package)
    {
        foreach (KeyValuePair<string, Entry> kv in _seen)
            if (string.Equals(kv.Key, package, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public void Reset()
    {
        _seen.Clear();
        MessagesSeen = 0;
        Supported = null;
        SubscriptionSent = null;
        SubscriptionVerb = null;
        Negotiated = false;
    }

    /// <summary>Everything seen, most-recently-active first.</summary>
    public IReadOnlyList<GmcpPackageSeen> Snapshot()
    {
        var list = new List<GmcpPackageSeen>(_seen.Count);
        foreach (KeyValuePair<string, Entry> kv in _seen)
            list.Add(new GmcpPackageSeen(kv.Key, kv.Value.Count, kv.Value.Last, kv.Value.LastAt));
        list.Sort((a, b) => b.LastAt.CompareTo(a.LastAt));
        return list;
    }

    /// <summary>The last payload of one package, matched case-insensitively so you can type
    /// <c>.gmcp char.vitals</c> without getting the capitalisation right.</summary>
    public GmcpPackageSeen? Find(string package)
    {
        foreach (GmcpPackageSeen p in Snapshot())
            if (string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Pretty-print JSON for reading in the output pane; returns the input unchanged
    /// when it is not JSON at all, which is itself worth seeing.</summary>
    public static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no payload)";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    /// <summary>One room the feed named.</summary>
    public sealed record RoomSeen(int Num, string Name, string Area);

    /// <summary>
    /// Every room <c>Room.Info</c> named, in the order first seen. This is what makes a capture
    /// answer "which areas did I actually walk through" — a question the last payload alone can
    /// never answer, however many hundred messages were counted behind it.
    /// </summary>
    public IReadOnlyList<RoomSeen> Rooms()
    {
        var rooms = new List<RoomSeen>();
        var byNum = new HashSet<int>();
        foreach (string payload in Distinct("Room.Info"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(payload);
                JsonElement r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                int num = r.TryGetProperty("num", out JsonElement n) && n.TryGetInt32(out int v) ? v : 0;
                if (num != 0 && !byNum.Add(num)) continue;   // the same room announced twice
                rooms.Add(new RoomSeen(
                    num,
                    r.TryGetProperty("name", out JsonElement nm) ? nm.ToString() : "",
                    r.TryGetProperty("area", out JsonElement ar) ? ar.ToString() : ""));
            }
            catch (JsonException) { /* a payload that is not a room is not a room */ }
        }
        return rooms;
    }

    /// <summary>The <c>.gmcp</c> report: negotiation, subscription, and the packages seen.</summary>
    /// <param name="mergeModeOf">Optional: how the state tree merges each package
    /// (<see cref="Scrye.Core.State.StateStore.MergeModeOf"/>). Shown per package so a plugin
    /// author can tell a whole-object package from a paged or snapshot/delta one — the
    /// difference between a bound gauge that holds and one that blinks.</param>
    public IReadOnlyList<string> Report(Func<string, string>? mergeModeOf = null)
    {
        var lines = new List<string> { "-- GMCP --" };

        if (!Negotiated)
        {
            lines.Add("  not negotiated: the server has not offered GMCP on this connection");
            lines.Add("  (if it should be, check that GMCP is enabled for this world)");
            return lines;
        }

        lines.Add("  negotiated: yes");
        lines.Add(SubscriptionSent is null
            ? "  subscription: NOT SENT — nothing will arrive; this is a client bug, please report it"
            : $"  subscription: {SubscriptionVerb} {SubscriptionSent}");

        if (Supported is null) lines.Add("  Core.Supported: no answer yet");
        else
        {
            lines.Add("  Core.Supported: " + Supported);
            // The server answers once before the subscription and once after, and the first is
            // every package set to 0 -- "subscribed to nothing". Seeing that as the LATEST
            // answer means the subscription did not take, which is the failure this whole
            // command exists to name, and it is invisible unless somebody says it out loud.
            if (SubscribedToNothing(Supported))
                lines.Add("  ...every package is 0: SUBSCRIBED TO NOTHING. Nothing will arrive.");
        }

        if (!AnyData)
        {
            lines.Add("  nothing has arrived yet.");
            lines.Add("  Packages only flow while subscribed AND when their values change, so");
            lines.Add("  quiet is normal for a moment — move a room or take damage to prod it.");
            return lines;
        }

        lines.Add($"  {MessagesSeen} message(s) across {PackagesSeen} package(s), newest first:");
        foreach (GmcpPackageSeen p in Snapshot())
        {
            string mode = mergeModeOf?.Invoke(p.Package) ?? "";
            mode = mode == "" || mode == "whole" ? "" : $" [{mode}]";
            lines.Add($"    {p.Package,-22} x{p.Count,-5} ({DistinctCount(p.Package)} distinct) "
                      + $"{p.LastAt:HH:mm:ss}  {Truncate(p.Last, 70)}{mode}");
        }

        IReadOnlyList<RoomSeen> rooms = Rooms();
        if (rooms.Count > 0)
        {
            var areas = new List<string>();
            foreach (RoomSeen r in rooms) if (!areas.Contains(r.Area)) areas.Add(r.Area);
            lines.Add($"  {rooms.Count} room(s) in {areas.Count} area(s): " + string.Join(", ", areas));
        }
        lines.Add("  '.gmcp <package>' for the whole of the last one; '.gmcp fields' for all of it.");
        return lines;
    }

    /// <summary>True when a <c>Core.Supported</c> payload says every package is off.</summary>
    public static bool SubscribedToNothing(string? supported)
    {
        if (string.IsNullOrWhiteSpace(supported)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(supported);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            bool any = false;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                any = true;
                if (p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() != 0) return false;
            }
            return any;
        }
        catch (JsonException) { return false; }
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>A markdown field report — every package, every leaf of its last payload, with
    /// the value seen. This is the artefact worth keeping from the first session on a new
    /// protocol: it says what the server ACTUALLY sends, which is the only thing worth writing
    /// a plugin against.</summary>
    public IReadOnlyList<string> FieldReport(string world)
    {
        var lines = new List<string>
        {
            $"# GMCP fields — {world}",
            "",
            $"Captured {DateTime.Now:yyyy-MM-dd HH:mm:ss}. " +
            $"{MessagesSeen} message(s) across {PackagesSeen} package(s).",
            "",
        };
        if (SubscriptionSent is not null)
            lines.Add($"Subscribed with `{SubscriptionVerb} {SubscriptionSent}`.");
        if (Supported is not null)
            lines.Add($"Server answered `Core.Supported {Supported}`.");
        lines.Add("");

        IReadOnlyList<RoomSeen> rooms = Rooms();
        if (rooms.Count > 0)
        {
            // Ahead of the packages, because on a capture taken to answer "where have I been"
            // this IS the answer, and the last payload of Room.Info is not.
            lines.Add("## Rooms visited");
            lines.Add("");
            var areas = new List<string>();
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (RoomSeen r in rooms)
            {
                if (!count.ContainsKey(r.Area)) { areas.Add(r.Area); count[r.Area] = 0; }
                count[r.Area]++;
            }
            lines.Add($"{rooms.Count} room(s) across {areas.Count} area(s).");
            lines.Add("");
            lines.Add("| area | rooms |");
            lines.Add("|---|---|");
            foreach (string a in areas) lines.Add($"| {Cell(a)} | {count[a]} |");
            lines.Add("");
            lines.Add("| # | area | room |");
            lines.Add("|---|---|---|");
            foreach (RoomSeen r in rooms) lines.Add($"| {r.Num} | {Cell(r.Area)} | {Cell(r.Name)} |");
            lines.Add("");
        }

        foreach (GmcpPackageSeen p in Snapshot())
        {
            lines.Add($"## {p.Package}");
            lines.Add("");
            int distinct = DistinctCount(p.Package);
            lines.Add($"{p.Count} message(s), {distinct} of them different, last at {p.LastAt:HH:mm:ss}.");
            lines.Add("");
            lines.Add("| field | state path | value |");
            lines.Add("|---|---|---|");
            foreach ((string path, string value) in Leaves(p.Last))
            {
                // An empty array contributes no leaves, so there is nothing in the state tree
                // to point at. Printing a path you cannot read would be worse than a dash.
                string where = value == EmptyArray ? "—" : "`" + Cell(StatePath(p.Package, path)) + "`";
                lines.Add($"| `{Cell(path)}` | {where} | {Cell(value)} |");
            }
            lines.Add("");
            lines.Add("```json");
            lines.Add(Pretty(p.Last));
            lines.Add("```");
            lines.Add("");

            // The rest of what this package sent. The table above describes ONE payload, and
            // one payload is a poor description of a package whose whole interest is how it
            // varies -- which rooms, which channels, which shapes.
            IReadOnlyList<string> all = Distinct(p.Package);
            if (all.Count > 1)
            {
                int show = Math.Min(all.Count, 12);
                lines.Add($"<details><summary>{all.Count} different payload(s)"
                          + (all.Count > show ? $", first {show}" : "") + "</summary>");
                lines.Add("");
                lines.Add("```json");
                for (int i = 0; i < show; i++) lines.Add(all[i]);
                lines.Add("```");
                lines.Add("");
                lines.Add("</details>");
                lines.Add("");
            }
        }
        return lines;
    }

    /// <summary>
    /// Where the state tree keeps a leaf, given the package and the field path as the SERVER
    /// spells it. The two are not the same string: the store lowercases every key and numbers
    /// array elements with a dot rather than brackets, so <c>Room.Contents</c>'s
    /// <c>items[0].name</c> is read as <c>room.contents.items.0.name</c>.
    ///
    /// <para>The report prints both columns for exactly that reason. The server's spelling is
    /// the evidence — it is what would show a <c>maxHP</c> where you expected <c>maxhp</c> —
    /// and the state path is the string you actually type into <c>scrye.getState</c>.</para>
    /// </summary>
    public static string StatePath(string package, string field)
    {
        string p = package.ToLowerInvariant() + (field.Length > 0 ? "." + field.ToLowerInvariant() : "");
        return System.Text.RegularExpressions.Regex.Replace(p, @"\[(\d+)\]", ".$1");
    }

    /// <summary>What an array with nothing in it reads as. It has no leaves, so the state tree
    /// holds no path for it at all — which is itself worth seeing in a report.</summary>
    public const string EmptyArray = "(empty array)";

    /// <summary>Flatten a payload to dotted leaf paths, in the SERVER's own spelling. Pair with
    /// <see cref="StatePath"/> for where the state tree keeps each one.</summary>
    public static IReadOnlyList<(string Path, string Value)> Leaves(string json)
    {
        var outp = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return outp;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Walk("", doc.RootElement, outp);
        }
        catch (JsonException) { outp.Add(("(not json)", json)); }
        return outp;
    }

    private static void Walk(string prefix, JsonElement e, List<(string, string)> outp)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in e.EnumerateObject())
                    Walk(prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name, prop.Value, outp);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in e.EnumerateArray())
                    Walk($"{prefix}[{i++}]", item, outp);
                if (i == 0) outp.Add((prefix, EmptyArray));
                break;
            default:
                outp.Add((prefix.Length == 0 ? "(value)" : prefix, e.ToString()));
                break;
        }
    }

    /// <summary>
    /// One cell of the field table. A pipe becomes the HTML entity rather than a backslash
    /// escape: an escape is not honoured inside a table by every renderer, and the report is
    /// evidence — a value that quietly came out as something other than what the server sent
    /// is worse than no report at all.
    ///
    /// <para>Not theoretical. <c>Room.Map</c>'s legend has <c>|</c> as both a key and a value,
    /// and its rows are drawn with it.</para>
    /// </summary>
    private static string Cell(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (char c in v)
        {
            if (c == '|') sb.Append("&#124;");
            else if (c is '\n' or '\r') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Length == 0 ? "*(empty)*" : sb.ToString();
    }
}
