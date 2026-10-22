using System.Diagnostics;

namespace Scrye.Core.Plugins;

/// <summary>A point-in-time health snapshot for one plugin, for the plugins manager.</summary>
public readonly record struct PluginHealth(
    string PluginId,
    long Calls,
    double TotalMs,
    double MaxMs,
    int SlowCalls,
    int Failures,
    int ConsecutiveFailures,
    string? LastError,
    bool Quarantined)
{
    /// <summary>Mean line-processing cost. 0 when the plugin has not been called yet.</summary>
    public double AverageMs => Calls > 0 ? TotalMs / Calls : 0;

    /// <summary>A one-line summary for the manager row; null when there is nothing worth saying.</summary>
    public string? Summary
    {
        get
        {
            if (Quarantined) return "quarantined — repeated errors";
            if (Failures > 0 && SlowCalls > 0) return $"{Failures} error(s) · {SlowCalls} slow";
            if (Failures > 0) return $"{Failures} error(s)";
            if (SlowCalls > 0) return $"{SlowCalls} slow call(s) · max {MaxMs:0}ms";
            return null;
        }
    }
}

/// <summary>
/// Per-plugin cost and failure accounting, and the quarantine decision.
///
/// <para><b>The problem this solves.</b> Every loaded plugin's <c>onLine</c> hooks and triggers
/// run synchronously on the session loop for every line the MUD sends. One plugin with a
/// pathological regex or a slow loop stalls that world's output directly, and until now nothing
/// measured it, so the symptom ("Scrye feels laggy on this character") had no path back to the
/// cause. Equally, a plugin whose callback throws on every line has its error swallowed into the
/// world output, once per line, forever.</para>
///
/// <para><b>Policy.</b> Two independent signals, both cheap:</para>
/// <list type="bullet">
/// <item><b>Slow</b> — a single call over <see cref="SlowCallMs"/> is counted and reported, but
/// rate-limited to one message per plugin per <see cref="SlowReportCooldown"/> so a consistently
/// slow plugin doesn't flood the very output it is slowing down.</item>
/// <item><b>Failing</b> — <see cref="QuarantineAfterConsecutiveFailures"/> consecutive failed
/// callbacks quarantines the plugin: the host unloads it and says why. Consecutive, not total,
/// because a plugin that throws on one unusual line a day is not broken; one that throws on
/// every line is. Any success resets the counter.</item>
/// <item><b>Failing often</b> — <see cref="QuarantineAfterFailuresInWindow"/> failures inside one
/// <see cref="FailureWindow"/> also quarantines, successes or not. The streak alone missed a
/// plugin that fails on every other line: each success reset it, so it printed an error line
/// every few lines for as long as the session ran (release review, 21 Oct 2026).</item>
/// <item><b>Error lines</b> — the runtimes ask <see cref="ShouldPrintError"/> before printing a
/// failure into the world output: the first <see cref="ErrorLinesPerWindow"/> per plugin per
/// window are shown, the rest are counted and summed up in one line when the window turns over.
/// The Plugins panel always has the last error.</item>
/// </list>
///
/// <para>Quarantine is deliberately not persisted — a reload or reconnect gives the plugin
/// another chance. The user disabling it is a decision; a bad line during a boss fight is not.</para>
///
/// <para>All mutation happens on the session loop thread, like everything else in the plugin
/// host, so there is no locking. <see cref="Snapshot"/> publishes an immutable array the UI
/// thread reads.</para>
/// </summary>
public sealed class PluginDiagnostics
{
    /// <summary>A single callback slower than this is counted as slow. Chosen against the
    /// frame budget: at 30fps the UI has ~33ms, so a plugin holding the loop for 50ms is
    /// already visible as a stutter on a busy screen.</summary>
    public const double SlowCallMs = 50;

    /// <summary>Consecutive failures before a plugin is unloaded.</summary>
    public const int QuarantineAfterConsecutiveFailures = 10;

    /// <summary>Minimum gap between "plugin X was slow" reports for the same plugin.</summary>
    public static readonly TimeSpan SlowReportCooldown = TimeSpan.FromSeconds(30);

    /// <summary>The window both the failure-rate quarantine and the error-line limit count over.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);

    /// <summary>Failures inside one <see cref="FailureWindow"/> that quarantine a plugin even
    /// though it succeeds in between. One a second for half a minute is not an unusual line.</summary>
    public const int QuarantineAfterFailuresInWindow = 30;

    /// <summary>Error lines a plugin may print into the world output per <see cref="FailureWindow"/>.</summary>
    public const int ErrorLinesPerWindow = 3;

    private sealed class Entry
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
        public int SlowCalls;
        public int Failures;
        public int ConsecutiveFailures;
        public string? LastError;
        public bool Quarantined;
        public long LastSlowReportTicks;
        public readonly Queue<double> RecentFailureMs = new();   // failure times inside the window
        public double PrintWindowStartMs = double.NegativeInfinity;
        public int PrintedInWindow;
        public int Hidden;                                        // error lines not shown this window
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Action<string> _report;

    /// <summary>How many calls may pass before the published snapshot is refreshed anyway, so a
    /// healthy plugin's average and call count don't sit frozen in the manager. Purely a
    /// staleness bound — correctness never depends on it.</summary>
    private const int RepublishEveryCalls = 256;
    private int _callsSincePublish;

    /// <summary>Immutable snapshot for the UI thread. Everything else here is loop-thread-only;
    /// this field is the one crossing the boundary, so it is replaced wholesale, never mutated.</summary>
    private volatile PluginHealth[] _published = Array.Empty<PluginHealth>();

    /// <summary>Ids quarantined since the last <see cref="TakeQuarantined"/>. The manager drains
    /// this after its dispatch loop finishes, because unloading a plugin mutates the runtime list
    /// that loop is iterating.</summary>
    private readonly List<string> _pendingQuarantine = new();

    private readonly Func<double> _nowMs;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <param name="report">Status sink — the same one plugin load/unload messages go to.</param>
    /// <param name="nowMs">Milliseconds on a monotonic clock; a test passes its own.</param>
    public PluginDiagnostics(Action<string> report, Func<double>? nowMs = null)
    {
        _report = report;
        _nowMs = nowMs ?? (() => Clock.Elapsed.TotalMilliseconds);
    }

    private Entry For(string pluginId)
    {
        if (!_entries.TryGetValue(pluginId, out Entry? e)) _entries[pluginId] = e = new Entry();
        return e;
    }

    /// <summary>Record a completed callback. <paramref name="elapsedTicks"/> is a raw
    /// <see cref="Stopwatch"/> delta so the hot path never allocates or divides.</summary>
    public void RecordCall(string pluginId, long elapsedTicks)
    {
        Entry e = For(pluginId);
        double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        e.Calls++;
        e.TotalMs += ms;
        if (ms > e.MaxMs) e.MaxMs = ms;
        if (++_callsSincePublish >= RepublishEveryCalls) Publish();
        if (ms >= SlowCallMs)
        {
            e.SlowCalls++;
            long now = Stopwatch.GetTimestamp();
            double sinceReport = (now - e.LastSlowReportTicks) * 1000.0 / Stopwatch.Frequency;
            if (e.LastSlowReportTicks == 0 || sinceReport >= SlowReportCooldown.TotalMilliseconds)
            {
                e.LastSlowReportTicks = now;
                _report($"plugin '{pluginId}' took {ms:0}ms to process a line " +
                        $"(avg {e.TotalMs / e.Calls:0.0}ms over {e.Calls} lines) — it is holding up output for this world");
            }
            Publish();
        }
    }

    /// <summary>Record a callback that succeeded, resetting the consecutive-failure streak.
    /// Only republishes when the streak was actually non-zero — the common case is a no-op.</summary>
    public void RecordSuccess(string pluginId)
    {
        Entry e = For(pluginId);
        if (e.ConsecutiveFailures == 0) return;
        e.ConsecutiveFailures = 0;
        Publish();
    }

    /// <summary>
    /// Record a thrown callback. Returns true when this failure crossed the quarantine
    /// threshold, in which case the id is queued for <see cref="TakeQuarantined"/>.
    /// </summary>
    public bool RecordFailure(string pluginId, string what, string message)
    {
        Entry e = For(pluginId);
        e.Failures++;
        e.ConsecutiveFailures++;
        e.LastError = $"{what}: {message}";
        double now = _nowMs();
        e.RecentFailureMs.Enqueue(now);
        while (e.RecentFailureMs.Count > 0 && now - e.RecentFailureMs.Peek() >= FailureWindow.TotalMilliseconds)
            e.RecentFailureMs.Dequeue();
        Publish();

        if (e.Quarantined) return false;
        string why;
        if (e.ConsecutiveFailures >= QuarantineAfterConsecutiveFailures)
            why = $"failed {e.ConsecutiveFailures} times in a row";
        else if (e.RecentFailureMs.Count >= QuarantineAfterFailuresInWindow)
            why = $"failed {e.RecentFailureMs.Count} times in the last {FailureWindow.TotalSeconds:0} seconds";
        else return false;

        e.Quarantined = true;
        _pendingQuarantine.Add(pluginId);
        Publish();
        _report($"plugin '{pluginId}' {why} " +
                $"(last: {e.LastError}) — unloading it. Fix the script and press Reload in the Plugins panel.");
        return true;
    }

    /// <summary>
    /// Ask before printing a failure into the world output: true for the first
    /// <see cref="ErrorLinesPerWindow"/> per plugin per <see cref="FailureWindow"/>. The first
    /// line held back says so; when a new window opens, the count held back is reported first.
    /// Call it once per failure, next to <see cref="RecordFailure"/>.
    /// </summary>
    public bool ShouldPrintError(string pluginId)
    {
        Entry e = For(pluginId);
        double now = _nowMs();
        if (now - e.PrintWindowStartMs >= FailureWindow.TotalMilliseconds)
        {
            if (e.Hidden > 0)
                _report($"plugin '{pluginId}': {e.Hidden} more error(s) were not shown");
            e.PrintWindowStartMs = now;
            e.PrintedInWindow = 0;
            e.Hidden = 0;
        }
        if (e.PrintedInWindow < ErrorLinesPerWindow) { e.PrintedInWindow++; return true; }
        if (++e.Hidden == 1)
            _report($"plugin '{pluginId}' keeps failing — its errors are hidden for the rest of the minute; " +
                    "the Plugins panel shows the last one");
        return false;
    }

    /// <summary>Drain the ids awaiting unload. Call after a dispatch loop, never during one.</summary>
    public IReadOnlyList<string> TakeQuarantined()
    {
        if (_pendingQuarantine.Count == 0) return Array.Empty<string>();
        string[] ids = _pendingQuarantine.ToArray();
        _pendingQuarantine.Clear();
        return ids;
    }

    /// <summary>True when the plugin has been quarantined and should not be reloaded implicitly.</summary>
    public bool IsQuarantined(string pluginId) =>
        _entries.TryGetValue(pluginId, out Entry? e) && e.Quarantined;

    /// <summary>Forget a plugin's history — called on an explicit reload, which is the user
    /// saying "I fixed it". Without this a quarantined plugin could never come back.</summary>
    public void Reset(string pluginId)
    {
        _entries.Remove(pluginId);
        Publish();
    }

    /// <summary>Health for one plugin (all-zero when it has never run).</summary>
    public PluginHealth Get(string pluginId) =>
        _entries.TryGetValue(pluginId, out Entry? e)
            ? new PluginHealth(pluginId, e.Calls, e.TotalMs, e.MaxMs, e.SlowCalls,
                               e.Failures, e.ConsecutiveFailures, e.LastError, e.Quarantined)
            : new PluginHealth(pluginId, 0, 0, 0, 0, 0, 0, null, false);

    /// <summary>The last published snapshot — safe to read from the UI thread.</summary>
    public PluginHealth[] Snapshot() => _published;

    /// <summary>Rebuild the published snapshot. Loop-thread only.</summary>
    public void Publish()
    {
        _callsSincePublish = 0;
        var result = new PluginHealth[_entries.Count];
        int i = 0;
        foreach (string id in _entries.Keys) result[i++] = Get(id);
        _published = result;
    }
}
