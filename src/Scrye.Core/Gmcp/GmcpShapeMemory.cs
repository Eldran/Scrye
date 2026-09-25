using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrye.Core.Gmcp;

/// <summary>
/// What the GMCP feed has looked like before: every package and every field path this MUD
/// has ever sent Scrye, kept between sessions, so a new one can be named the day it appears.
///
/// <para>It exists because the server changes under us and says nothing. Guild.Fleet went
/// quiet in September 2026 and came back a week later; Guild.City grew <c>production</c>;
/// <c>Char.XP</c> sat in <c>Core.Supported</c> unsent for a day because nobody had asked for
/// it by name. Every one of those was found by reading a field report line by line against
/// the one before. This does the comparing: a field or package that has never been seen is
/// announced once in the output as it arrives, and the field report opens with what changed.</para>
///
/// <para>A shape, not a value: paths only, with array indices folded (<c>items[3].name</c> is
/// <c>items[].name</c>), numbered slices folded (<c>hird_0</c>, <c>hird_1</c> are
/// <c>hird_#</c>), and the few maps whose KEYS are data (a room's exits, the map legend)
/// folded to <c>*</c> - otherwise every new exit name would be a "new field".</para>
///
/// <para>The first session with no memory is the baseline: it learns quietly and announces
/// nothing, because on that evening everything is new and nothing is news.</para>
/// </summary>
public sealed class GmcpShapeMemory
{
    // "Package" for a package, "Package|path" for a field. Case kept: two spellings of one
    // name is exactly what a report like this should show, not fold away.
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);   // before this session
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);    // this session
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly List<string> _newInOrder = new();                       // this session, first seen first

    /// <summary>Maps whose keys are data rather than field names, as "Package|path".</summary>
    private static readonly HashSet<string> KeyedMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Room.Info|exits",      // n, s, jump, portal, nexus, gswap ... one per exit the room has
        "Room.Map|legend",      // one per glyph the server chose to explain
    };

    /// <summary>More new fields than this in one message are summarised, not listed.</summary>
    public const int ListAtMost = 8;

    /// <summary>Called with a line to show when something never seen before arrives.</summary>
    public Action<string>? Announce { get; set; }

    /// <summary>Whether new shapes are announced as they arrive (<c>.gmcp watch on|off</c>).
    /// The memory learns either way.</summary>
    public bool Watch { get; set; } = true;

    /// <summary>True while there was nothing remembered: the session that builds the baseline.</summary>
    public bool Baseline => _known.Count == 0;

    public int KnownCount => _known.Count;

    /// <summary>What this session has sent that no session before it did, in arrival order.</summary>
    public IReadOnlyList<string> NewThisSession => _newInOrder;

    /// <summary>The key a field is remembered under: indices, slices and keyed maps folded.</summary>
    public static string FieldKey(string package, string path)
    {
        string p = Regex.Replace(path, @"\[\d+\]", "[]");
        string[] parts = p.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (Regex.IsMatch(seg, @"^\d+$")) parts[i] = "#";                       // a numbered key
            else parts[i] = Regex.Replace(seg, @"_\d+(?=(\[\])?$)", "_#");          // hird_0, market_1
        }
        // a keyed map: everything past it is one wildcard
        for (int i = 0; i < parts.Length; i++)
        {
            string head = string.Join('.', parts, 0, i + 1);
            if (KeyedMaps.Contains(package + "|" + head))
            {
                p = head + (i + 1 < parts.Length ? ".*" : "");
                return package + "|" + p;
            }
        }
        return package + "|" + string.Join('.', parts);
    }

    /// <summary>
    /// Learn one payload. Call it for payloads not seen before on this connection (the audit's
    /// distinct set) - an identical payload has an identical shape.
    /// </summary>
    public void Observe(string package, string json)
    {
        // Core.Supported's keys are package names, not fields; it is compared on its own.
        if (string.Equals(package, "Core.Supported", StringComparison.OrdinalIgnoreCase)) return;

        bool quiet = Baseline || !Watch;
        if (_seen.Add(package) && !_known.Contains(package))
        {
            _newInOrder.Add(package);
            if (!quiet && _announced.Add(package))
                Announce?.Invoke($"GMCP: new package {package} - never sent on this MUD before ('.gmcp {package}' shows it)");
        }

        List<string>? fresh = null;
        foreach ((string path, string value) in GmcpAudit.Leaves(json))
        {
            if (path == "(not json)" || path == "(value)") continue;
            // An empty list and a full one are the same field: the empty one is keyed as the
            // list itself ("carts[]"), and every element path marks its lists as seen quietly,
            // so a list that is empty today is not "new" because it was full yesterday.
            string key = FieldKey(package, value == GmcpAudit.EmptyArray ? path + "[0]" : path);
            for (int at = key.IndexOf("[]", StringComparison.Ordinal); at >= 0 && at + 2 < key.Length;
                 at = key.IndexOf("[]", at + 2, StringComparison.Ordinal))
                _seen.Add(key[..(at + 2)]);                     // the list itself: seen, never news
            if (!_seen.Add(key) || _known.Contains(key)) continue;
            _newInOrder.Add(key);
            // a field of a package that is itself new is not news twice over
            if (!_known.Contains(package)) continue;
            if (!quiet && _announced.Add(key)) (fresh ??= new()).Add(key[(package.Length + 1)..]);
        }
        if (fresh is { Count: > 0 })
        {
            string list = fresh.Count <= ListAtMost
                ? string.Join(", ", fresh)
                : string.Join(", ", fresh.Take(ListAtMost)) + $" and {fresh.Count - ListAtMost} more";
            Announce?.Invoke($"GMCP: new field{(fresh.Count == 1 ? "" : "s")} in {package}: {list}");
        }
    }

    /// <summary>Everything remembered: what was known plus what this session learned.</summary>
    public string ToJson()
    {
        var all = new SortedSet<string>(_known, StringComparer.Ordinal);
        all.UnionWith(_seen);
        return JsonSerializer.Serialize(new { version = 1, shapes = all });
    }

    /// <summary>Load a memory written by <see cref="ToJson"/>. Garbage loads as nothing - the
    /// next session is then a baseline, which is the honest reading of a memory that is gone.</summary>
    public void LoadJson(string? json)
    {
        _known.Clear();
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("shapes", out JsonElement s)
                && s.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in s.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } k) _known.Add(k);
        }
        catch (JsonException) { _known.Clear(); }
    }

    /// <summary>
    /// Learn from a saved <c>.gmcp fields</c> report: every payload in it (the pretty-printed
    /// last one and the one-line different ones under it) goes straight into what is KNOWN,
    /// as if an earlier session had sent it. So a memory started today knows what every
    /// capture of the last month saw, and the first "new field" it reports is a real one.
    /// Returns the number of package and field shapes that were not known before.
    /// </summary>
    public int LearnFromReport(string markdown)
    {
        int before = _known.Count;
        string? package = null;
        var block = new System.Text.StringBuilder();
        bool inJson = false;
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!inJson)
            {
                // a package heading is one word with a dot in it ("## Guild.City"); the report's
                // other headings ("## Rooms visited", "## Changes since ...") are not packages
                Match h = Regex.Match(line, @"^## (\S+)$");
                if (h.Success) { package = h.Groups[1].Value.Contains('.') ? h.Groups[1].Value : null; continue; }
                if (line.StartsWith("```json", StringComparison.Ordinal)) { inJson = true; block.Clear(); }
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inJson = false;
                if (package is not null && !string.Equals(package, "Core.Supported", StringComparison.OrdinalIgnoreCase))
                    LearnBlock(package, block.ToString());
                continue;
            }
            block.Append(line).Append('\n');
        }
        // what this session called new and a report already knew is not new after all
        _newInOrder.RemoveAll(k => _known.Contains(k));
        return _known.Count - before;
    }

    // One ```json block: the whole of it when it is one payload (the pretty-printed last one),
    // else one payload per line (the "different payloads" list).
    private void LearnBlock(string package, string text)
    {
        if (TryLearn(package, text)) return;
        foreach (string line in text.Split('\n'))
            if (line.TrimStart().StartsWith('{')) TryLearn(package, line);
    }

    private bool TryLearn(string package, string json)
    {
        try { using JsonDocument _ = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }
        _known.Add(package);
        foreach ((string path, string value) in GmcpAudit.Leaves(json))
        {
            if (path == "(not json)" || path == "(value)") continue;
            string key = FieldKey(package, value == GmcpAudit.EmptyArray ? path + "[0]" : path);
            for (int at = key.IndexOf("[]", StringComparison.Ordinal); at >= 0 && at + 2 < key.Length;
                 at = key.IndexOf("[]", at + 2, StringComparison.Ordinal))
                _known.Add(key[..(at + 2)]);
            _known.Add(key);
        }
        return true;
    }

    /// <summary>A new connection: what the last one learned becomes known, and the session
    /// sets start over - so a reconnect does not announce the same field twice.</summary>
    public void NewSession()
    {
        _known.UnionWith(_seen);
        _seen.Clear();
        _announced.Clear();
        _newInOrder.Clear();
    }

    /// <summary>
    /// Fields remembered for a package that arrived this session without them. Only packages
    /// that did arrive are asked about: a package you never prodded says nothing about its
    /// fields. Not proof of removal - an optional field (a target, a campaign) comes and goes -
    /// so the report words it as "not seen", and it is only a hint.
    /// </summary>
    public IReadOnlyList<string> KnownButNotSeen()
    {
        var packagesNow = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in _seen) if (!k.Contains('|')) packagesNow.Add(k);
        var outp = new List<string>();
        foreach (string k in _known)
        {
            int bar = k.IndexOf('|');
            if (bar < 0) continue;
            if (packagesNow.Contains(k[..bar]) && !_seen.Contains(k)) outp.Add(k);
        }
        outp.Sort(StringComparer.Ordinal);
        return outp;
    }

    /// <summary>Packages remembered from earlier sessions that have not arrived in this one.</summary>
    public IReadOnlyList<string> PackagesNotSeen()
    {
        var outp = new List<string>();
        foreach (string k in _known) if (!k.Contains('|') && !_seen.Contains(k)) outp.Add(k);
        outp.Sort(StringComparer.Ordinal);
        return outp;
    }
}
