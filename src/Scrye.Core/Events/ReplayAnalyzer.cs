using Scrye.Core.Automation;

namespace Scrye.Core.Events;

/// <summary>
/// One recorded line paired with what its triggers did THEN (in the recording)
/// versus what they WOULD do NOW (against a supplied, current rule set). The heart
/// of "replay this session against my current triggers" (roadmap #3).
/// </summary>
public sealed record ReplayLineAnalysis
{
    /// <summary>Sequence number of the recorded line event.</summary>
    public long Seq { get; init; }
    /// <summary>The recorded line text (what triggers match against).</summary>
    public string Line { get; init; } = "";
    /// <summary>Trigger names that fired against this line in the recording.</summary>
    public IReadOnlyList<string> FiredThen { get; init; } = Array.Empty<string>();
    /// <summary>Trigger names that would fire now, plus their action, from a dry-run.</summary>
    public IReadOnlyList<AutomationHit> WouldFireNow { get; init; } = Array.Empty<AutomationHit>();

    /// <summary>True when the set of trigger names differs between then and now.</summary>
    public bool Differs
    {
        get
        {
            var then = new HashSet<string>(FiredThen, StringComparer.Ordinal);
            var now = new HashSet<string>(WouldFireNow.Select(h => h.Name), StringComparer.Ordinal);
            return !then.SetEquals(now);
        }
    }

    /// <summary>Trigger names present now but not then (newly matching).</summary>
    public IReadOnlyList<string> Added =>
        WouldFireNow.Select(h => h.Name).Where(n => !FiredThen.Contains(n)).Distinct().ToArray();

    /// <summary>Trigger names present then but not now (no longer matching).</summary>
    public IReadOnlyList<string> Removed =>
        FiredThen.Where(n => WouldFireNow.All(h => h.Name != n)).Distinct().ToArray();
}

/// <summary>
/// Replays a recorded session's lines through a <em>current</em>
/// <see cref="AutomationEngine"/> (loaded with today's triggers) and diffs the
/// result against what the recording shows actually fired. Pure analysis — uses
/// the side-effect-free <see cref="AutomationEngine.Simulate"/>, so it never sends
/// or mutates anything. Lets a user test trigger changes against a real past fight
/// without reconnecting.
/// </summary>
public static class ReplayAnalyzer
{
    /// <summary>
    /// Walk the recording in order. Each <see cref="SessionEventKind.LineReceived"/>
    /// or <see cref="SessionEventKind.Prompt"/> starts a new line; the
    /// <see cref="SessionEventKind.TriggerMatched"/> events that follow it (until the
    /// next line/input) are what fired THEN. <paramref name="engine"/> supplies what
    /// would fire NOW.
    /// </summary>
    public static IReadOnlyList<ReplayLineAnalysis> Analyze(SessionRecording recording, AutomationEngine engine)
    {
        var result = new List<ReplayLineAnalysis>();
        List<string>? currentFired = null;

        foreach (SessionEvent ev in recording.Events)
        {
            switch (ev.Kind)
            {
                case SessionEventKind.LineReceived:
                case SessionEventKind.Prompt:
                    currentFired = new List<string>();
                    result.Add(new ReplayLineAnalysis
                    {
                        Seq = ev.Seq,
                        Line = ev.Text,
                        FiredThen = currentFired,
                        WouldFireNow = engine.Simulate(ev.Text),
                    });
                    break;

                case SessionEventKind.TriggerMatched:
                    // belongs to the most recent line; Label carries the trigger name
                    if (currentFired is not null && !string.IsNullOrEmpty(ev.Label))
                        currentFired.Add(ev.Label!);
                    break;

                case SessionEventKind.InputSubmitted:
                    // input breaks the line→trigger association
                    currentFired = null;
                    break;
            }
        }

        return result;
    }

    /// <summary>Only the lines whose trigger behaviour changed between then and now.</summary>
    public static IReadOnlyList<ReplayLineAnalysis> Diffs(SessionRecording recording, AutomationEngine engine) =>
        Analyze(recording, engine).Where(a => a.Differs).ToList();
}
