namespace Scrye.Core.Plugins;

/// <summary>
/// The version of the plugin-facing contract — the <c>scrye.*</c> script surface, the
/// widget vocabulary in <see cref="PanelSpec"/>, the manifest schema, and the state-path
/// conventions plugins bind to.
///
/// <para>This is deliberately NOT the application version. Scrye 2.4 and Scrye 3.1 can
/// both speak plugin API 1.3; a plugin declares what it needs and the host decides whether
/// it can load. Semantic versioning applies to the API, not the client:</para>
/// <list type="bullet">
/// <item><b>Minor</b> bump — additions only. New <c>scrye.*</c> functions, new widget types,
/// new optional manifest fields. Every existing plugin keeps working.</item>
/// <item><b>Major</b> bump — something was removed or changed meaning. Plugins written for
/// the previous major are expected to break.</item>
/// </list>
///
/// <para><b>When you change the API, change this.</b> Adding a widget type or a
/// <c>scrye.*</c> function without bumping <see cref="Current"/> means a plugin that
/// requires the new feature cannot express that requirement, and gets a runtime error deep
/// inside its script instead of a clear refusal at load.</para>
/// </summary>
public static class ScryeApi
{
    /// <summary>The plugin API version this build implements.</summary>
    /// <remarks>
    /// History:
    /// <list type="bullet">
    /// <item><c>1.0</c> — the original surface: output/input, state + watches, variables,
    /// hooks, timers, rules, store, panels with label/value/text/gauge/progress/button/
    /// buttonrow/input/colorgrid/barlist widgets.</item>
    /// <item><c>1.1</c> — semantic theme tokens accepted anywhere a <c>#RRGGBB</c> colour is,
    /// the <c>list</c> and <c>table</c> widgets, and the <c>requires</c>/<c>permissions</c>
    /// manifest fields.</item>
    /// </list>
    /// </remarks>
    // 1.2 — inline colour markup (Scrye.Core.Text.Markup) is honoured by scrye.print and
    //       scrye.capture. Additive: a 1.1 plugin's plain strings are unaffected, since a
    //       string with no '@' is passed through untouched.
    // 1.3 — text widgets honour the same markup; colorgrid gains `labels` (draw these chars
    //       on their tiles); buttonrow gains a bound form (`bind` + `onClick(label, index)`)
    //       whose buttons come from state. All additive.
    // 1.4 — a plugin may declare data files in its manifest ("data": { key: file }); the host
    //       reads them from the plugin folder and publishes them as scrye.data.<key>. Additive:
    //       a plugin that declares none gets an empty scrye.data table.
    // 1.5 — scrye.onIdle(fn): the client's idle guard fired, so stop whatever this plugin is
    //       driving. The guard is off unless the profile turns it on, so a plugin that never
    //       registers a handler is unaffected.
    // 1.6 — the automapper batch (all additive):
    //       * scrye.onCommand(fn) — observe (never veto) every command sent to the MUD,
    //         whatever sent it: typed input, macros, sequences, triggers, plugins.
    //       * scrye.json.encode/decode — a host-provided JSON codec, so plugins stop
    //         hand-rolling serialization for scrye.store and data interchange.
    //       * scrye.store.setMany{...} — N keys, one disk write.
    //       * scrye.emit(name, data) / scrye.on(name, fn) — inter-plugin events.
    //       * colorgrid onHover(col, row, ch) — pointer-over cell callbacks (desktop only;
    //         touch devices never fire it).
    //       * sub-second timers: scrye.after/every now honour fractional seconds; the
    //         scheduler ticks at 250 ms, so that is the effective resolution and floor.
    // 1.7 — colorgrid `weave = true`: even cells render as full tiles, odd cells as thin
    //       connector lines ('-', '|', '/', '\', 'x' in their palette colour) — a map draws
    //       rooms on even cells and the exits between them on the odd cells they share.
    //       Additive: an unwoven grid renders exactly as before, and text-rendering hosts
    //       (the companion) ignore the flag and show the same characters as an ASCII map.
    // ---- engine note (no version bump): between 1.7 builds the Lua ENGINE changed from
    //      MoonSharp (Lua 5.2-ish, managed) to native Lua 5.4 via KeraLua
    //      (docs/Plan-KeraLua-Migration.md). The scrye.* surface is identical, which is why
    //      Current did not move — but scripts may now use 5.3/5.4 language features
    //      (integer division //, bitwise operators, goto, utf8, integer subtype), and
    //      string.format("%d", x) now errors when x has a fractional part.
    // 1.8 — colorgrid `icons = { ["char"] = "glyph", ... }`: micro-icons. An iconed cell
    //       draws a muted tile of its palette colour with a tiny host-drawn vector glyph on
    //       top (vocabulary: water dashes grass hill tree pine mountain house tower gate
    //       ruin star person ship anchor flag bolt crown hammer cross dot). Icons beat
    //       `labels` letters for the same character; cells under 8 px fall back to plain
    //       tiles, then the letter rules. Additive: unknown names render as plain tiles and
    //       text-rendering hosts (the companion) ignore the map. Also colorgrid `cell = N`:
    //       raises the 12 px cell-size ceiling (clamped 3-64) for charts whose icons
    //       deserve room; cells still shrink to fit the panel width. And the `row`
    //       container widget (`{ type = "row", widgets = {...} }`): children laid out
    //       side by side, each at its measured width -- a chart with its notes beside
    //       it instead of below. Hosts that can't lay out rows may stack or skip them.
    // 1.9 — `onRightClick` on colorgrid, button and buttonrow widgets: a SECOND, distinct
    //       callback, never a variant of onClick. On a grid it receives the same
    //       (col, row, ch) as onClick; on a button it takes no arguments, like onClick.
    //       Additive: a widget that declares none behaves exactly as before.
    //
    //       Note this also FIXES a bug rather than only adding to the surface — colorgrid
    //       cell clicks used to fire on any pointer button, so a right-click silently ran
    //       onClick. Left click now runs onClick and nothing else. A plugin that relied on
    //       the old behaviour was relying on an accident, but it is a behaviour change.
    //
    //       Unlike onHover, this is NOT desktop-only and so may gate real actions: the
    //       companion maps a ~500 ms long-press onto it, so anything reachable by
    //       right-click on the desktop is reachable by touch on the phone.
    // 1.10 — scrye.onRelay(fn) / scrye.onRelay("Tell", fn): chat arriving from ANOTHER open
    //        world, because this world's tab is the one in front. Handlers get
    //        (sourceWorld, channel, message) — same filtered-hook shape as onChannel, one extra
    //        leading argument. Which channels a world relays is its own RelayChannels setting,
    //        tells only by default.
    //
    //        It is a SEPARATE hook rather than more onChannel traffic because a plugin that
    //        treated foreign chat as its own would notify twice (the source world already did),
    //        write another MUD's lines into its own log, and fold them into its own history.
    //        Additive: a plugin that registers no handler never sees a relay.
    // 1.11 — manifest "panes": ["Chats"]. Capture panes a plugin writes to, created when it
    //        loads instead of the first time a line happens to be routed into one.
    //
    //        Lazy creation reads as a bug on a fresh machine: enable the chat plugin, see
    //        nothing, and the pane only turns up when somebody eventually speaks. Declaring it
    //        makes "this plugin has a Chats pane" true at load.
    //
    //        Additive both ways: an older host ignores the field, and a pane the user closed by
    //        hand is not resurrected (the world layout remembers which). Traffic still wins - a
    //        line routed to a closed pane recreates it, because then it has content to show.
    // 1.12 — a barlist row may carry a seventh field: "qty,pct;qty,pct;..." rawest first.
    //        The fill is then drawn as one segment per quality stage — width from the
    //        quantity, colour from where that quality lands on the raw->refined ramp —
    //        instead of the single amber/green split the fifth field implies.
    //
    //        That split was always an average: a refinery holding 120 raw and 30 finished
    //        units drew the same bar as one holding 150 units all half-done. The stages are
    //        what the player is actually watching, so the bar now shows them.
    //
    //        Additive: a row without the field renders exactly as it did before, and a host
    //        that predates it ignores the extra column. Ordering carries the meaning as much
    //        as hue does — rawest is always leftmost — so the ramp is never the only encoding.
    public static readonly Version Current = new(1, 12);

    /// <summary>The API version as it appears in manifests and diagnostics ("1.5").</summary>
    public static string CurrentText => $"{Current.Major}.{Current.Minor}";

    /// <summary>
    /// Can this build load a plugin with the given manifest? True when it declares no
    /// <c>requires.scryeApi</c> (the original case — everything loads) or when the declared range
    /// admits <see cref="Current"/>. On false, <paramref name="reason"/> is a message fit to show
    /// the user verbatim.
    ///
    /// <para>A malformed range is a refusal, not a shrug: an author who wrote a constraint meant
    /// something by it, and guessing which direction they meant is worse than saying it didn't
    /// parse.</para>
    ///
    /// <para>This lives here rather than on the host's plugin descriptor so the rule and the
    /// version it checks against stay in the same assembly — the one a plugin author references.</para>
    /// </summary>
    public static bool IsCompatible(PluginManifest manifest, out string reason)
    {
        reason = "";
        string? spec = manifest.Requires?.ScryeApi;
        if (!ApiRange.TryParse(spec, out ApiRange? range, out string error))
        {
            reason = $"its manifest requires scryeApi \"{spec}\", which is not a valid range ({error})";
            return false;
        }
        if (range is null) return true;                       // no constraint declared
        if (range.Allows(Current)) return true;

        reason = $"it requires plugin API {range.Text} and this build provides {CurrentText}";
        return false;
    }
}

/// <summary>
/// A version range from a manifest's <c>requires.scryeApi</c>, e.g. <c>"&gt;=1.1 &lt;2.0"</c>.
///
/// <para>Grammar: space-separated constraints, all of which must hold. Each constraint is an
/// operator (<c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c>) followed by a
/// version, or a bare version (treated as <c>&gt;=</c>). A version is <c>major</c> or
/// <c>major.minor</c>. Examples:</para>
/// <list type="bullet">
/// <item><c>"1.1"</c> — API 1.1 or newer (including 2.x; add an upper bound if you care).</item>
/// <item><c>"&gt;=1.1 &lt;2.0"</c> — the usual shape: needs a 1.1 feature, expects 2.0 to break it.</item>
/// <item><c>"=1.1"</c> — exactly 1.1. Rarely what you want.</item>
/// </list>
///
/// <para>An absent <c>requires</c> block means "no constraint" — every plugin written before
/// this existed keeps loading, which is why <see cref="TryParse"/> maps null/empty to a null
/// range rather than an error.</para>
/// </summary>
public sealed class ApiRange
{
    private readonly List<(string Op, Version Version)> _constraints;

    /// <summary>The original text, for diagnostics.</summary>
    public string Text { get; }

    private ApiRange(string text, List<(string, Version)> constraints)
    {
        Text = text;
        _constraints = constraints;
    }

    /// <summary>
    /// Parse a range. Returns true for a well-formed spec (with <paramref name="range"/> set)
    /// AND for a null/whitespace spec (with <paramref name="range"/> null, meaning "unconstrained").
    /// Returns false only for a spec that was supplied but is malformed — that is a manifest
    /// bug the author should hear about, not something to silently ignore.
    /// </summary>
    public static bool TryParse(string? spec, out ApiRange? range, out string error)
    {
        range = null;
        error = "";
        if (string.IsNullOrWhiteSpace(spec)) return true;   // no constraint declared

        var constraints = new List<(string, Version)>();
        foreach (string token in spec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim();
            string op = ">=";
            int i = 0;
            if (t.StartsWith(">=", StringComparison.Ordinal) || t.StartsWith("<=", StringComparison.Ordinal))
            {
                op = t[..2];
                i = 2;
            }
            else if (t.Length > 0 && (t[0] == '>' || t[0] == '<' || t[0] == '='))
            {
                op = t[..1];
                i = 1;
            }

            string versionText = t[i..].Trim();
            if (!TryParseVersion(versionText, out Version? v))
            {
                error = $"'{token}' is not a version constraint";
                return false;
            }
            constraints.Add((op, v!));
        }

        if (constraints.Count == 0)
        {
            error = "empty range";
            return false;
        }
        range = new ApiRange(spec.Trim(), constraints);
        return true;
    }

    /// <summary>Accepts "1", "1.2" and "1.2.3"; normalises to a two-or-more component Version.</summary>
    private static bool TryParseVersion(string s, out Version? version)
    {
        version = null;
        if (s.Length == 0) return false;
        if (!s.Contains('.')) s += ".0";              // "1" -> "1.0"
        if (!Version.TryParse(s, out Version? v)) return false;
        // Version.TryParse leaves Build/Revision at -1 when absent; normalise so comparisons
        // between "1.1" and "1.1.0" don't surprise anyone.
        version = new Version(v.Major, v.Minor);
        return true;
    }

    /// <summary>Does <paramref name="candidate"/> satisfy every constraint?</summary>
    public bool Allows(Version candidate)
    {
        var c = new Version(candidate.Major, candidate.Minor);
        foreach ((string op, Version v) in _constraints)
        {
            bool ok = op switch
            {
                ">=" => c >= v,
                ">" => c > v,
                "<=" => c <= v,
                "<" => c < v,
                "=" => c == v,
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    public override string ToString() => Text;
}
