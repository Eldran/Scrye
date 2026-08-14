namespace Scrye.Core.Mip;

/// <summary>One thing the audit noticed about a MIP feed key.</summary>
/// <param name="Key">The BBE key, e.g. "BATTLE".</param>
/// <param name="Severity">"drift" (a real mismatch) or "note" (informational).</param>
/// <param name="Detail">What is wrong, phrased for someone reading it in the output pane.</param>
/// <param name="Sample">A truncated sample of the value that triggered it.</param>
public sealed record MipShapeFinding(string Key, string Severity, string Detail, string Sample);

/// <summary>
/// What a feed key's value should look like structurally. Only the SHAPE is checked — field
/// counts and record layout — because that is what silently breaks a positional parser: if the
/// server inserts a field into a unit record, <c>bid</c> quietly becomes <c>ord</c> and nothing
/// throws.
/// </summary>
/// <param name="Key">The BBE key this describes.</param>
/// <param name="MinFields">Minimum count after splitting on <see cref="Delimiter"/>. Zero to skip.</param>
/// <param name="Delimiter">Top-level separator.</param>
/// <param name="Source">Where the expectation came from, quoted in the report so a false alarm
/// can be traced to the code that claimed it rather than argued about.</param>
/// <param name="Records">Optional check on a field that holds a record list.</param>
public sealed record MipShapeExpectation(
    string Key,
    int MinFields,
    char Delimiter,
    string Source,
    MipRecordExpectation? Records = null);

/// <summary>A record list living inside one field: records separated by <see cref="Separator"/>,
/// each split on <see cref="Delimiter"/> into one of <see cref="AllowedCounts"/> fields.</summary>
/// <param name="FieldIndex">1-based index of the containing field.</param>
public sealed record MipRecordExpectation(
    int FieldIndex, char Separator, char Delimiter, int[] AllowedCounts);

/// <summary>
/// Watches the MIP viking feed and reports when a key's structure stops matching what the
/// parsers assume. Two independent detectors, because each catches what the other cannot:
///
/// <list type="number">
/// <item><b>Expectations</b> — a hand-written table of layouts read off the parsers. Catches
/// drift that ALREADY happened, including on the very first value seen. Deliberately small:
/// every entry names its source, and a table that guessed at keys nobody verified would
/// produce false alarms, which is worse than no tool at all.</item>
/// <item><b>Stability</b> — the shape of every key is remembered at first sight and compared on
/// every later value. Needs no table, covers every key including ones nothing knows about, and
/// catches a mid-session change. Its blind spot is the mirror image: if a key was already
/// wrong before this session started, it looks perfectly stable.</item>
/// </list>
///
/// <para>Cheap enough to leave running: one dictionary lookup and a couple of character counts
/// per feed value, on the session loop.</para>
/// </summary>
public sealed class MipShapeAudit
{
    /// <summary>
    /// Layouts verified against the parsers in this repo. Each is traceable to the code that
    /// reads it — see the Source string. Add a row when you verify another key; do NOT add one
    /// from inference, because a wrong expectation costs more than a missing one.
    /// </summary>
    public static readonly MipShapeExpectation[] Known =
    {
        // BATTLE: active|phase|turn|warpoints|mode|target|budget:spent|w:h:dz|terrain|works|units
        new("BATTLE", 11, '|', "nille-viking battle(), 11 pipe fields since the works[] field was added",
            new MipRecordExpectation(11, ';', ',', new[] { 6, 9 })),   // reserve=6, fielded=9

        // territory_map(): w|h|px|py|onmap. The 4-field form is the documented legacy shape for
        // an empty map, so 4 is the floor and 5 is what a live map sends.
        new("VMAPH", 4, '|', "nille-viking territory_map(), 4 legacy / 5 current"),

        // VMAPL: POI records, each type|name|x|y|owner
        new("VMAPL", 0, '\0', "nille-viking territory_map() POI records",
            new MipRecordExpectation(1, ';', '|', new[] { 5 })),

        // draw_army(): levy|cap|used|conscripts|conscript_cap then unit records
        new("ARMY", 5, '|', "nille-viking army()"),

        // draw_bonds()/standings: lineage records id|name|score|standing|own
        new("STANDINGS", 0, '\0', "nille-viking standings()",
            new MipRecordExpectation(1, ';', '|', new[] { 5 })),
    };

    private readonly Dictionary<string, MipShapeExpectation> _expected =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Shape, string Sample)> _firstSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MipShapeFinding> _findings = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public MipShapeAudit(IEnumerable<MipShapeExpectation>? expectations = null)
    {
        foreach (MipShapeExpectation e in expectations ?? Known) _expected[e.Key] = e;
    }

    /// <summary>Keys seen at least once this session.</summary>
    public int KeysSeen => _firstSeen.Count;

    /// <summary>Everything noticed so far, in the order it was noticed.</summary>
    public IReadOnlyList<MipShapeFinding> Findings => _findings;

    /// <summary>Feed one decoded viking key/value. Safe to call for every message.</summary>
    public void Observe(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || value is null) return;

        string shape = Shape(value);
        if (_firstSeen.TryGetValue(key, out (string Shape, string Sample) prev))
        {
            // A key whose shape moves mid-session is drift by definition — no table needed, and
            // no way for it to be a stale expectation on our side.
            if (prev.Shape != shape)
            {
                Add("drift", key,
                    $"shape changed mid-session: was {prev.Shape}, now {shape}", value);
                _firstSeen[key] = (shape, Truncate(value));
            }
            return;                       // expectations are only checked on first sight
        }

        _firstSeen[key] = (shape, Truncate(value));

        if (!_expected.TryGetValue(key, out MipShapeExpectation? exp))
        {
            Add("note", key, $"no recorded expectation; observed {shape}", value);
            return;
        }

        string? problem = Check(exp, value);
        if (problem is not null)
            Add("drift", key, $"{problem}  (expected per: {exp.Source})", value);
    }

    /// <summary>Validate one value against an expectation. Null when it looks right.</summary>
    private static string? Check(MipShapeExpectation exp, string value)
    {
        string[] fields = exp.Delimiter == '\0'
            ? new[] { value }
            : value.Split(exp.Delimiter);

        if (exp.MinFields > 0 && fields.Length < exp.MinFields)
            return $"expected at least {exp.MinFields} '{exp.Delimiter}' fields, saw {fields.Length}";

        if (exp.Records is not { } rec) return null;
        if (rec.FieldIndex < 1 || rec.FieldIndex > fields.Length)
            return $"expected a record list in field {rec.FieldIndex}, but there are only {fields.Length} fields";

        string list = fields[rec.FieldIndex - 1];
        if (list.Length == 0) return null;                       // an empty list is not drift

        foreach (string record in list.Split(rec.Separator))
        {
            if (record.Length == 0) continue;
            int count = record.Split(rec.Delimiter).Length;
            if (Array.IndexOf(rec.AllowedCounts, count) < 0)
                return $"a record had {count} '{rec.Delimiter}' fields; expected "
                     + string.Join(" or ", rec.AllowedCounts);
        }
        return null;
    }

    /// <summary>A compact structural fingerprint: the top-level delimiter and field count, plus
    /// the record layout when the value is a list. Two values with the same fingerprint parse
    /// the same way, which is the only property being tracked.</summary>
    public static string Shape(string value)
    {
        if (value.Length == 0) return "empty";

        // '|' wins when both are present: the 3Scapes feeds put pipes at the top level and use
        // ';' for records INSIDE a field (BATTLE is exactly this shape).
        if (value.Contains('|')) return "|" + value.Split('|').Length;
        if (value.Contains(';'))
        {
            var counts = new SortedSet<int>();
            char sub = value.Contains(',') ? ',' : value.Contains('|') ? '|' : ':';
            foreach (string r in value.Split(';'))
                if (r.Length > 0) counts.Add(r.Split(sub).Length);
            return counts.Count == 0 ? ";0" : $";*{sub}{string.Join("/", counts)}";
        }
        if (value.Contains(',')) return "," + value.Split(',').Length;
        if (value.Contains(':')) return ":" + value.Split(':').Length;
        return "flat";
    }

    private void Add(string severity, string key, string detail, string sample)
    {
        // One finding per key+detail: a feed that repeats every few seconds would otherwise
        // bury the report in duplicates of the same news.
        if (!_reported.Add(severity + "" + key + "" + detail)) return;
        _findings.Add(new MipShapeFinding(key, severity, detail, Truncate(sample)));
    }

    private static string Truncate(string s) =>
        s.Length <= 120 ? s : s[..120] + "...";

    /// <summary>The report, as lines ready to print. Drift first, because that is the answer to
    /// "did something change"; the notes below it are the keys nothing has an opinion about.</summary>
    public IReadOnlyList<string> Report()
    {
        var lines = new List<string>();
        var drift = _findings.Where(f => f.Severity == "drift").ToList();
        var notes = _findings.Where(f => f.Severity != "drift").ToList();

        lines.Add($"MIP feed audit: {KeysSeen} key(s) seen, {drift.Count} possible drift, "
                + $"{notes.Count} unrecorded.");

        if (KeysSeen == 0)
        {
            lines.Add("  Nothing observed yet - the viking feed arrives on BBE messages, so this");
            lines.Add("  stays empty until 'vtoggle' has some feeds on and the server has sent one.");
            return lines;
        }

        if (drift.Count > 0)
        {
            lines.Add("");
            lines.Add("  DRIFT - these no longer match what the parsers assume:");
            foreach (MipShapeFinding f in drift)
            {
                lines.Add($"    {f.Key}: {f.Detail}");
                lines.Add($"      sample: {f.Sample}");
            }
        }

        if (notes.Count > 0)
        {
            lines.Add("");
            lines.Add($"  No recorded expectation ({notes.Count}) - shape logged so a later change");
            lines.Add("  would still be caught, but nothing here has been checked against a parser:");
            foreach (MipShapeFinding f in notes)
                lines.Add($"    {f.Key}: {f.Detail}");
        }

        if (drift.Count == 0)
        {
            lines.Add("");
            lines.Add("  No drift found in the keys that have one. Note this can only speak for");
            lines.Add("  keys the server actually sent this session.");
        }
        return lines;
    }
}
