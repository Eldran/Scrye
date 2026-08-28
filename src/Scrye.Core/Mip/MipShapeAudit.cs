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

        // WSTOCK: effective warehouse capacity, then good|amount|quality with an optional
        // qualifier ("7367;furs|301|100;mead|60|100|aged"). The 1 is that leading capacity
        // field, and it is in the list on purpose: not knowing it was there made
        // 3s-viking-status draw a stock row called "7367", and not knowing what it meant left
        // both plugins sizing the warehouse from a base-per-tier table that is only right until
        // the character raises a storage skill.
        new("WSTOCK", 0, '\0', "3s-viking-status warehouse() + its Trade tabs at_warehouse()/stock",
            new MipRecordExpectation(1, ';', '|', new[] { 1, 3, 4 })),
    };

    private readonly Dictionary<string, MipShapeExpectation> _expected =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Shape, string Sample)> _firstSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MipShapeFinding> _findings = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>Per-tag tally for the field report: how many frames, whether this build decodes
    /// it, and the first payload seen (the sample a plugin author works backwards from).</summary>
    private sealed class TagInfo
    {
        public int Count;
        public bool Handled;
        public string Sample = "";
    }

    private readonly Dictionary<string, TagInfo> _tags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The latest value of every feed key, so the report can show what a key holds
    /// right now rather than only what it held the first time. Keys are cheap; a feed is a few
    /// dozen of them.</summary>
    private readonly Dictionary<string, string> _latest = new(StringComparer.OrdinalIgnoreCase);

    public MipShapeAudit(IEnumerable<MipShapeExpectation>? expectations = null)
    {
        foreach (MipShapeExpectation e in expectations ?? Known) _expected[e.Key] = e;
    }

    /// <summary>Keys seen at least once this session.</summary>
    public int KeysSeen => _firstSeen.Count;

    /// <summary>Everything noticed so far, in the order it was noticed.</summary>
    public IReadOnlyList<MipShapeFinding> Findings => _findings;

    /// <summary>Tags seen at least once this session.</summary>
    public int TagsSeen => _tags.Count;

    /// <summary>Note one arriving MIP frame by tag. Cheap enough for every message; this is what
    /// lets the field report name a tag this build has no decoder for.</summary>
    public void ObserveTag(string tag, string data, bool handled)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (!_tags.TryGetValue(tag, out TagInfo? info))
            _tags[tag] = info = new TagInfo { Handled = handled, Sample = Truncate(data ?? "") };
        info.Count++;
        // A payload arriving empty first would otherwise leave the report with no sample at all.
        if (info.Sample.Length == 0 && !string.IsNullOrEmpty(data)) info.Sample = Truncate(data);
    }

    /// <summary>Feed one decoded viking key/value. Safe to call for every message.</summary>
    public void Observe(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || value is null) return;

        _latest[key] = value;
        string shape = Shape(value);

        // An empty value means the list is empty right now, not that its structure moved. Check()
        // has always known this ("an empty list is not drift"); the stability detector did not,
        // so a cart queue draining and refilling reported drift on every transition. Neither
        // recorded nor compared: the next real value sets or checks the shape.
        if (shape == "empty") return;

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

    /// <summary>
    /// A compact structural fingerprint: the record delimiter and the distinct per-record field
    /// counts. Two values with the same fingerprint parse the same way, which is the only
    /// property being tracked.
    ///
    /// <para><b>What it deliberately does not encode is how many records there are.</b> That was
    /// the original version's mistake: it counted delimiters across the whole payload, so a
    /// building list that went from three entries to two changed fingerprint and was reported as
    /// drift. Nearly every viking feed is a list whose length changes constantly — buildings,
    /// map POIs, carts, stock, status effects — so the detector reported drift for all of them
    /// and real drift would have been invisible in the noise.</para>
    ///
    /// <para>Known limit: for a list whose records are legitimately of several widths (WSTOCK,
    /// where a qualified good has an extra field), the fingerprint is the SET of widths, so a
    /// server change that moved a record onto a width the set already contains would slip
    /// through. Catching that needs per-record-type expectations, which is what the
    /// <see cref="Known"/> table is for.</para>
    /// </summary>
    public static string Shape(string value)
    {
        if (value.Length == 0) return "empty";

        // A whitespace-separated list of k:v tokens — STFX is "[aeg:293 skad:682 ...]gray:",
        // one token per active effect. Handled first because splitting THAT on ':' just counts
        // the effects, which is exactly the number that is supposed to move.
        if (PairListShape(value) is { } pairs) return pairs;

        // Everything else is one or more ';'-separated records — one record when there is no
        // ';' at all, which is what makes a single-entry list fingerprint the same as a
        // multi-entry one. The sub-delimiter is chosen once for the whole value rather than per
        // record, so a header line and its record list stay comparable (BATTLE is that shape:
        // an 11-field header whose last field runs into a ';' list of comma records, giving a
        // stable "|1/11" however many units are on the field).
        char sub = value.Contains('|') ? '|' : value.Contains(',') ? ',' : ':';
        var counts = new SortedSet<int>();
        foreach (string r in value.Split(';'))
            if (r.Length > 0) counts.Add(r.Split(sub).Length);

        if (counts.Count == 0) return "empty";                       // ";;;" and friends

        // Chunks with no sub-delimiter at all are dropped once any chunk has one. They carry no
        // field structure to check, and keeping them made the fingerprint depend on list length
        // again by the back door: BATTLE's header runs into its first unit record, so a single
        // unit gives widths {11} and two give {11,1}. WSTOCK has the same shape the other way up,
        // a bare stock total ahead of its goods. Dropping them makes both count-independent.
        if (counts.Max > 1) counts.Remove(1);
        if (counts.Max == 1) return "flat";        // no delimiters anywhere, list of scalars or not

        return sub + string.Join("/", counts);
    }

    /// <summary>Fingerprint a whitespace-separated list of <c>name:value</c> tokens, or null when
    /// the value is not one. Returns the distinct per-token field counts, so the fingerprint is
    /// the same whether eight effects are active or thirteen.</summary>
    private static string? PairListShape(string value)
    {
        if (!value.Contains(' ')) return null;
        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;

        var counts = new SortedSet<int>();
        int withColon = 0;
        foreach (string tok in tokens)
        {
            int n = tok.Split(':').Length;
            if (n > 1) withColon++;
            counts.Add(n);
        }
        // Most tokens carrying a colon is what makes this a pair list rather than prose that
        // happens to contain a space.
        return withColon * 2 >= tokens.Length ? "sp:" + string.Join("/", counts) : null;
    }

    // ---- the field report ------------------------------------------------------------------

    /// <summary>
    /// What this session has actually seen, written for someone about to build a plugin for a
    /// guild nobody here plays. <see cref="Report"/> answers "did the feed change?"; this answers
    /// the prior question, "what does this character even get?".
    ///
    /// <para>Three sections, because MIP carries three different kinds of thing. The
    /// <b>vitals</b> are the fixed per-character slots every guild fills in (FFF) — the same
    /// eight numbers whatever you play, with the guild-specific meaning hidden behind names like
    /// "gp1". The <b>feed keys</b> are BBE's key/value pairs, which is where a guild puts
    /// whatever it likes. The <b>tags</b> are the frame types themselves, listed whether or not
    /// this build decodes them: a tag nothing understands is exactly the interesting case, and
    /// before this existed the only trace of one was a raw line scrolling past in the event log.
    /// </para>
    ///
    /// <para>Every row carries a live sample, because a shape fingerprint tells you a value has
    /// four pipe-separated fields and a sample tells you what they mean.</para>
    /// </summary>
    /// <param name="vitals">The MIP-owned variables, in the order to print them. Null values are
    /// shown as "not sent", which is itself a finding — Vikings never report SP.</param>
    /// <param name="markdown">Render as a markdown document to save and share, rather than as
    /// lines for the output pane. The markdown form allows longer samples.</param>
    public IReadOnlyList<string> FieldReport(
        IReadOnlyList<(string Name, string? Value)> vitals, bool markdown = false)
    {
        int sampleWidth = markdown ? 400 : 52;
        var lines = new List<string>();
        void H(string plain, string md) => lines.Add(markdown ? md : plain);

        H($"MIP field report: {TagsSeen} tag(s), {_latest.Count} feed key(s) seen this session.",
          "# MIP field report");
        if (markdown)
        {
            lines.Add("");
            lines.Add($"{TagsSeen} tag(s) and {_latest.Count} feed key(s) seen this session. "
                    + "Samples are live values, truncated.");
        }

        // --- vitals ---------------------------------------------------------------------
        lines.Add("");
        H("  VITALS (FFF) - the fixed slots every guild fills in:",
          "## Vitals (FFF)\n\nThe fixed per-character slots every guild fills in.\n\n| field | value |\n|---|---|");
        foreach ((string name, string? value) in vitals)
        {
            string shown = value is null ? "(not sent)" : value.Length == 0 ? "(empty)" : Clip(value, sampleWidth);
            H($"    {name,-10} {shown}", $"| `{name}` | {Escape(shown)} |");
        }

        // --- feed keys ------------------------------------------------------------------
        lines.Add("");
        if (_latest.Count == 0)
        {
            H("  FEED KEYS: none yet - BBE carries these, so nothing arrives until the guild's",
              "## Feed keys (BBE)\n\nNone seen yet. BBE carries these, so nothing arrives until the guild's");
            H("  feeds are switched on server-side (on 3Scapes, 'vtoggle').",
              "feeds are switched on server-side (on 3Scapes, `vtoggle`).");
        }
        else
        {
            H($"  FEED KEYS ({_latest.Count}) - read from a plugin as scrye.getState(\"vik.<key>\"):",
              "## Feed keys (BBE)\n\nRead from a plugin as `scrye.getState(\"vik.<key>\")` — lower-cased. "
            + "The `vik.` prefix is historical: BBE is the generic carrier and every guild's keys land there.\n"
            + "\n| key | shape | sample |\n|---|---|---|");
            foreach (string key in _latest.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string value = _latest[key];
                string shape = _firstSeen.TryGetValue(key, out (string Shape, string Sample) f) ? f.Shape : "empty";
                // The shape fingerprint is literally made of delimiters, so it needs escaping
                // as much as the sample does — "|3" would otherwise split the table cell.
                H($"    {key,-14} {shape,-8} {Clip(value, sampleWidth)}",
                  $"| `{Escape(key)}` | `{Escape(shape)}` | {Escape(Clip(value, sampleWidth))} |");
            }
        }

        // --- tags -----------------------------------------------------------------------
        lines.Add("");
        H("  TAGS - every frame type that arrived:",
          "## Tags\n\nEvery frame type that arrived, decoded or not.\n"
        + "\n| tag | frames | decoded | sample |\n|---|---|---|---|");
        foreach (string tag in _tags.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            TagInfo t = _tags[tag];
            string state = t.Handled ? "decoded" : $"NOT DECODED -> mip.{tag.ToLowerInvariant()}";
            H($"    {tag,-5} x{t.Count,-6} {state}",
              $"| `{tag}` | {t.Count} | {(t.Handled ? "yes" : $"**no** — `mip.{tag.ToLowerInvariant()}`")} "
            + $"| {Escape(Clip(t.Sample, sampleWidth))} |");
            if (!markdown && !t.Handled && t.Sample.Length > 0)
                lines.Add($"      sample: {Clip(t.Sample, sampleWidth)}");
        }

        if (_tags.Values.Any(t => !t.Handled))
        {
            lines.Add("");
            H("  An undecoded tag's raw payload is readable as state mip.<tag>, so a plugin can",
              "\nAn undecoded tag's raw payload is readable as state `mip.<tag>`, so a plugin can "
            + "use it before this client learns to decode it.");
            if (!markdown)
                lines.Add("  use it before this client learns to decode it.");
        }
        return lines;
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    /// <summary>Markdown table cells cannot contain a raw pipe, and MIP is full of them.</summary>
    private static string Escape(string s) => s.Replace("|", "\\|");

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
            // Packed several to a line. One line per key meant a real feed buried the drift
            // section - the part you actually came for - under two hundred lines of inventory.
            var cells = notes
                .Select(f => f.Key + "=" + f.Detail[(f.Detail.LastIndexOf(' ') + 1)..])
                .ToList();
            var row = new System.Text.StringBuilder("   ");
            foreach (string cell in cells)
            {
                if (row.Length + cell.Length + 2 > 88) { lines.Add(row.ToString()); row.Clear().Append("   "); }
                row.Append(' ').Append(cell);
            }
            if (row.Length > 3) lines.Add(row.ToString());
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
