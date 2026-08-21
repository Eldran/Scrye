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
    }

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

    /// <summary>The <c>.gmcp</c> report: negotiation, subscription, and the packages seen.</summary>
    public IReadOnlyList<string> Report()
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

        lines.Add(Supported is null
            ? "  Core.Supported: no answer yet"
            : "  Core.Supported: " + Supported);

        if (!AnyData)
        {
            lines.Add("  nothing has arrived yet.");
            lines.Add("  Packages only flow while subscribed AND when their values change, so");
            lines.Add("  quiet is normal for a moment — move a room or take damage to prod it.");
            return lines;
        }

        lines.Add($"  {MessagesSeen} message(s) across {PackagesSeen} package(s), newest first:");
        foreach (GmcpPackageSeen p in Snapshot())
            lines.Add($"    {p.Package,-22} x{p.Count,-5} {p.LastAt:HH:mm:ss}  {Truncate(p.Last, 80)}");
        lines.Add("  '.gmcp <package>' for the whole of the last one.");
        return lines;
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

        foreach (GmcpPackageSeen p in Snapshot())
        {
            lines.Add($"## {p.Package}");
            lines.Add("");
            lines.Add($"{p.Count} message(s), last at {p.LastAt:HH:mm:ss}.");
            lines.Add("");
            lines.Add("| field | value |");
            lines.Add("|---|---|");
            foreach ((string path, string value) in Leaves(p.Last))
                lines.Add($"| `{path}` | {Cell(value)} |");
            lines.Add("");
            lines.Add("```json");
            lines.Add(Pretty(p.Last));
            lines.Add("```");
            lines.Add("");
        }
        return lines;
    }

    /// <summary>Flatten a payload to dotted leaf paths, the way the state tree does, so what the
    /// report lists is what a plugin can actually read.</summary>
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
                if (i == 0) outp.Add((prefix, "(empty array)"));
                break;
            default:
                outp.Add((prefix.Length == 0 ? "(value)" : prefix, e.ToString()));
                break;
        }
    }

    private static string Cell(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (char c in v) sb.Append(c == '|' ? '\\' : c == '\n' ? ' ' : c);
        if (c_is_pipe(v)) { }
        return sb.Length == 0 ? "*(empty)*" : sb.ToString();
    }

    private static bool c_is_pipe(string _) => false;
}
