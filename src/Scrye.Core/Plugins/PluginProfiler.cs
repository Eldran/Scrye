using System.Diagnostics;
using System.Globalization;

namespace Scrye.Core.Plugins;

/// <summary>What a profiled span was doing.</summary>
public enum ProfileKind
{
    /// <summary>A plugin callback: <c>onLine</c>, a trigger's <c>run</c>, <c>onGmcp</c>, a timer.</summary>
    Handler,
    /// <summary>A <c>scrye.*</c> call from the plugin into the client.</summary>
    Api,
    /// <summary>The client matching a plugin's trigger or alias pattern against a line.</summary>
    Match,
}

/// <summary>One row of the profile: where time went, for one handler / API call / pattern.</summary>
/// <param name="SelfMs">Time spent here and nowhere deeper: a handler's own script time without
/// the <c>scrye.*</c> calls it made, an API call's own time without the handlers it set off.</param>
/// <param name="TotalMs">Wall time from entry to exit, everything underneath included.</param>
public readonly record struct ProfileRow(
    string PluginId, ProfileKind Kind, string What, string? Detail,
    long Calls, double SelfMs, double TotalMs, double MaxMs)
{
    /// <summary>The row's name as the report prints it: <c>onGmcp Char.Vitals</c>, <c>scrye.addPanel</c>.</summary>
    public string Label => Detail is { Length: > 0 } ? $"{What} {Detail}" : What;
}

/// <summary>
/// Where the plugins' time goes, one level below <see cref="PluginDiagnostics"/>.
///
/// <para><b>The problem this solves.</b> Diagnostics says "plugin X took 80 ms on a line" and
/// only when one call is that slow. It cannot say which handler it was, whether the time was
/// the script's own or a <c>scrye.addPanel</c> it called, or notice eight plugins that each
/// take 3 ms on every line. This keeps a running total per handler, per API call and per
/// trigger pattern, so <c>.profile</c> can rank them.</para>
///
/// <para><b>Self and total.</b> Spans nest: a handler calls <c>scrye.emit</c>, which runs
/// another plugin's <c>on:</c> handler, which calls <c>scrye.addPanel</c>. Each span's
/// <i>total</i> is its wall time; its <i>self</i> time is the total minus the spans opened
/// inside it. Self times therefore add up to the real time spent in plugins - nothing is
/// counted twice - and that is the column the report sorts by.</para>
///
/// <para><b>Cost.</b> Two <see cref="Stopwatch.GetTimestamp"/> reads and a few additions per
/// span, no allocation. Hot callers (every trigger on every line, every API binding) hold
/// their <see cref="Slot"/> instead of looking it up. Off (<see cref="Enabled"/> false) is one
/// branch.</para>
///
/// <para>Loop-thread only, like the rest of the plugin host: no locking.</para>
/// </summary>
public sealed class PluginProfiler
{
    /// <summary>A row's counters. Callers on a hot path fetch one with <see cref="For"/> once and
    /// keep it; <see cref="Reset()"/> zeroes slots in place, so a held slot stays valid.</summary>
    public sealed class Slot
    {
        internal readonly string PluginId;
        internal readonly ProfileKind Kind;
        internal readonly string What;
        internal readonly string? Detail;
        internal long Calls;
        internal long SelfTicks;
        internal long TotalTicks;
        internal long MaxTicks;

        internal Slot(string pluginId, ProfileKind kind, string what, string? detail)
        {
            PluginId = pluginId;
            Kind = kind;
            What = what;
            Detail = detail;
        }
    }

    private readonly record struct Key(string PluginId, ProfileKind Kind, string What, string? Detail);

    private struct Frame
    {
        public long Start;
        public long ChildTicks;
    }

    /// <summary>Detail text (a regex, a state path) is cut to this in a row's name.</summary>
    public const int MaxDetailLength = 60;

    private readonly Dictionary<Key, Slot> _slots = new();
    private Frame[] _stack = new Frame[32];
    private int _depth;
    private readonly Func<long> _now;
    private readonly double _ticksPerMs;
    private long _since;

    /// <summary>Whether spans are measured. Turning it off keeps what was gathered.</summary>
    public bool Enabled { get; set; } = true;

    /// <param name="now">A monotonic tick source and its frequency, for tests; the default is
    /// <see cref="Stopwatch"/>.</param>
    /// <param name="ticksPerSecond">Ticks per second of <paramref name="now"/>.</param>
    public PluginProfiler(Func<long>? now = null, long ticksPerSecond = 0)
    {
        _now = now ?? Stopwatch.GetTimestamp;
        _ticksPerMs = (now is null || ticksPerSecond <= 0 ? Stopwatch.Frequency : ticksPerSecond) / 1000.0;
        _since = _now();
    }

    /// <summary>The profiler's clock, for a caller timing a <see cref="Leaf"/> itself.</summary>
    public long Now() => _now();

    /// <summary>The counters for one row, created on first use.</summary>
    public Slot For(string pluginId, ProfileKind kind, string what, string? detail = null)
    {
        if (detail is { Length: > MaxDetailLength }) detail = detail[..(MaxDetailLength - 1)] + "…";
        var key = new Key(pluginId, kind, what, detail);
        if (!_slots.TryGetValue(key, out Slot? s)) _slots[key] = s = new Slot(pluginId, kind, what, detail);
        return s;
    }

    /// <summary>Open a span. Returns a token for <see cref="Leave"/>, or -1 when profiling is
    /// off (Leave then does nothing). Always pair them, in a <c>finally</c> where the code
    /// between can throw.</summary>
    public int Enter()
    {
        if (!Enabled) return -1;
        if (_depth == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
        _stack[_depth].Start = _now();
        _stack[_depth].ChildTicks = 0;
        return _depth++;
    }

    /// <summary>Close the span <paramref name="token"/> opened and book it on <paramref name="slot"/>.
    /// Spans still open above it (a caller that lost its Leave to an exception) are dropped.</summary>
    public void Leave(int token, Slot slot)
    {
        if (token < 0 || token >= _depth) return;
        _depth = token;
        long elapsed = _now() - _stack[token].Start;
        if (elapsed < 0) elapsed = 0;
        long self = elapsed - _stack[token].ChildTicks;
        if (self < 0) self = 0;
        if (token > 0) _stack[token - 1].ChildTicks += elapsed;
        slot.Calls++;
        slot.SelfTicks += self;
        slot.TotalTicks += elapsed;
        if (elapsed > slot.MaxTicks) slot.MaxTicks = elapsed;
    }

    /// <summary>Book a span that had nothing inside it, timed by the caller - one timestamp
    /// pair around a pattern match, without the stack. Counts against the enclosing span.</summary>
    public void Leaf(Slot slot, long elapsedTicks)
    {
        if (!Enabled) return;
        if (elapsedTicks < 0) elapsedTicks = 0;
        if (_depth > 0) _stack[_depth - 1].ChildTicks += elapsedTicks;
        slot.Calls++;
        slot.SelfTicks += elapsedTicks;
        slot.TotalTicks += elapsedTicks;
        if (elapsedTicks > slot.MaxTicks) slot.MaxTicks = elapsedTicks;
    }

    /// <summary>Start over: every counter to zero, the measuring window from now.</summary>
    public void Reset()
    {
        foreach (Slot s in _slots.Values) Zero(s);
        _since = _now();
    }

    /// <summary>Zero one plugin's rows - on reload, when its handlers are new ones. The slots
    /// stay registered: a runtime that is still loaded holds them, and a row that is never
    /// called again is not shown.</summary>
    public void Reset(string pluginId)
    {
        foreach (KeyValuePair<Key, Slot> kv in _slots)
            if (string.Equals(kv.Key.PluginId, pluginId, StringComparison.Ordinal)) Zero(kv.Value);
    }

    private static void Zero(Slot s)
    {
        s.Calls = 0;
        s.SelfTicks = 0;
        s.TotalTicks = 0;
        s.MaxTicks = 0;
    }

    /// <summary>Milliseconds since the profiler was created or last reset.</summary>
    public double WindowMs => (_now() - _since) / _ticksPerMs;

    /// <summary>Every row that has been called, most self time first.</summary>
    public IReadOnlyList<ProfileRow> Rows()
    {
        var rows = new List<ProfileRow>(_slots.Count);
        foreach (Slot s in _slots.Values)
            if (s.Calls > 0)
                rows.Add(new ProfileRow(s.PluginId, s.Kind, s.What, s.Detail, s.Calls,
                                        s.SelfTicks / _ticksPerMs, s.TotalTicks / _ticksPerMs, s.MaxTicks / _ticksPerMs));
        rows.Sort(static (a, b) =>
        {
            int c = b.SelfMs.CompareTo(a.SelfMs);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.PluginId, b.PluginId);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
        return rows;
    }

    /// <summary>The <c>.profile</c> report as output lines: a line per plugin, then the
    /// <paramref name="top"/> costliest rows. <paramref name="filter"/> keeps only rows whose
    /// plugin id or name contains it (case-insensitive); the totals then cover those rows.</summary>
    public IReadOnlyList<string> Report(int top = DefaultTop, string? filter = null)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        IReadOnlyList<ProfileRow> all = Rows();
        var rows = new List<ProfileRow>(all.Count);
        foreach (ProfileRow r in all)
            if (string.IsNullOrEmpty(filter)
                || r.PluginId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || Name(r).Contains(filter, StringComparison.OrdinalIgnoreCase))
                rows.Add(r);

        var lines = new List<string>();
        double window = WindowMs;
        string state = Enabled ? "" : " (profiling is OFF - these are the numbers from before)";
        if (rows.Count == 0)
        {
            lines.Add(string.IsNullOrEmpty(filter)
                ? $"plugin profile: nothing measured in the last {Span(window)}{state}"
                : $"plugin profile: nothing matches '{filter}' in the last {Span(window)}{state}");
            return lines;
        }

        // Per plugin: script = handlers' own time, client = scrye.* calls, match = patterns.
        var perPlugin = new Dictionary<string, double[]>(StringComparer.Ordinal);
        double sum = 0;
        foreach (ProfileRow r in rows)
        {
            if (!perPlugin.TryGetValue(r.PluginId, out double[]? p)) perPlugin[r.PluginId] = p = new double[3];
            p[(int)r.Kind] += r.SelfMs;
            sum += r.SelfMs;
        }
        var ids = new List<string>(perPlugin.Keys);
        ids.Sort((a, b) =>
        {
            int c = Sum(perPlugin[b]).CompareTo(Sum(perPlugin[a]));
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });

        string share = window > 0 ? $" = {(sum / window * 100).ToString("0.##", inv)}% of the time" : "";
        string scope = string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'";
        lines.Add($"plugin profile{scope}: {Ms(sum)} in plugins over {Span(window)}{share}{state}");

        int idWidth = 6;
        foreach (string id in ids) idWidth = Math.Max(idWidth, id.Length);
        lines.Add($"  {"plugin".PadRight(idWidth)}  {"total",9}  {"script",9}  {"client",9}  {"match",9}");
        foreach (string id in ids)
        {
            double[] p = perPlugin[id];
            lines.Add($"  {id.PadRight(idWidth)}  {Ms(Sum(p)),9}  {Ms(p[(int)ProfileKind.Handler]),9}  " +
                      $"{Ms(p[(int)ProfileKind.Api]),9}  {Ms(p[(int)ProfileKind.Match]),9}");
        }

        int shown = Math.Min(Math.Max(top, 1), rows.Count);
        lines.Add(shown < rows.Count ? $"  top {shown} of {rows.Count} by own time:" : "  by own time:");
        lines.Add($"  {"own",9}  {"with calls",10}  {"calls",8}  {"avg",9}  {"max",9}  where");
        for (int i = 0; i < shown; i++)
        {
            ProfileRow r = rows[i];
            lines.Add($"  {Ms(r.SelfMs),9}  {Ms(r.TotalMs),10}  {r.Calls.ToString(inv),8}  " +
                      $"{Ms(r.TotalMs / r.Calls),9}  {Ms(r.MaxMs),9}  {r.PluginId}  {Name(r)}");
        }
        return lines;

        static double Sum(double[] p) => p[0] + p[1] + p[2];
    }

    /// <summary>How many rows <see cref="Report"/> shows when nobody says.</summary>
    public const int DefaultTop = 15;

    /// <summary>Read <c>.profile</c>'s report arguments: a number is how many rows, <c>all</c>
    /// is every row, anything else is the filter text (<c>.profile 3s-map 30</c>).</summary>
    public static void ParseReportArgs(string? arg, out int top, out string? filter)
    {
        top = DefaultTop;
        filter = null;
        foreach (string word in (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0) top = n;
            else if (word.Equals("all", StringComparison.OrdinalIgnoreCase)) top = int.MaxValue;
            else filter = filter is null ? word : filter + " " + word;
        }
    }

    /// <summary>A row's name with its kind made visible: <c>scrye.store.set</c>,
    /// <c>match ^You kill</c>, <c>onGmcp Char.Vitals</c>.</summary>
    private static string Name(ProfileRow r) => r.Kind switch
    {
        ProfileKind.Api => "scrye." + r.Label,
        ProfileKind.Match => "match " + r.Label,
        _ => r.Label,
    };

    private static string Ms(double ms)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        if (ms >= 10_000) return (ms / 1000).ToString("0.0", inv) + " s";
        if (ms >= 100) return ms.ToString("0", inv) + " ms";
        if (ms >= 1) return ms.ToString("0.0", inv) + " ms";
        if (ms >= 0.01) return ms.ToString("0.00", inv) + " ms";
        return (ms * 1000).ToString("0.0", inv) + " µs";
    }

    private static string Span(double ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
