using Scrye.Core.Automation;
using Scrye.Core.Events;
using Xunit;

namespace Scrye.Core.Tests;

public class ReplayAnalyzerTests
{
    // Build a recording by driving lines through an engine (the "then" rules),
    // capturing LineReceived + TriggerMatched exactly as a live session would.
    private static SessionRecording Record(AutomationEngine engine, params string[] lines)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int i = 0;
        var bus = new EventBus { Clock = () => t.AddMilliseconds(100 * i++) };
        var rec = new SessionRecorder("W", t);
        bus.Subscribe(rec);
        engine.Hit += h => { if (h.Kind == AutomationHitKind.Trigger) bus.Emit(SessionEventKind.TriggerMatched, h.Input, h.Name, h.Action); };
        var actions = new NoOpActions();
        foreach (string line in lines)
        {
            bus.Emit(SessionEventKind.LineReceived, line);
            engine.ProcessLine(line, actions);
        }
        return new SessionRecording(rec.Header, rec.Events.ToArray());
    }

    private sealed class NoOpActions : IWorldActions
    {
        public void Send(string text) { }
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    [Fact]
    public void UnchangedRulesProduceNoDiffs()
    {
        var then = new AutomationEngine(new VariableStore());
        then.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low*", Send = "flee" });
        var recording = Record(then, "you are low", "nothing here");

        var now = new AutomationEngine(new VariableStore());
        now.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low*", Send = "flee" });

        Assert.Empty(ReplayAnalyzer.Diffs(recording, now));
    }

    [Fact]
    public void AddedTriggerShowsAsDiff()
    {
        var then = new AutomationEngine(new VariableStore());
        then.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low*", Send = "flee" });
        var recording = Record(then, "a wiremouth lunges", "you are low");

        var now = new AutomationEngine(new VariableStore());
        now.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low*", Send = "flee" });
        now.AddTrigger(new TriggerDef { Name = "defend", Pattern = "*lunges*", Send = "defend" });

        var diffs = ReplayAnalyzer.Diffs(recording, now);
        ReplayLineAnalysis d = Assert.Single(diffs);
        Assert.Equal("a wiremouth lunges", d.Line);
        Assert.Contains("defend", d.Added);
        Assert.Empty(d.Removed);
    }

    [Fact]
    public void RemovedTriggerShowsAsDiff()
    {
        var then = new AutomationEngine(new VariableStore());
        then.AddTrigger(new TriggerDef { Name = "greet", Pattern = "* says hello", Send = "wave" });
        var recording = Record(then, "Bob says hello");

        var now = new AutomationEngine(new VariableStore());   // greet removed

        var diffs = ReplayAnalyzer.Diffs(recording, now);
        ReplayLineAnalysis d = Assert.Single(diffs);
        Assert.Contains("greet", d.Removed);
        Assert.Empty(d.Added);
    }

    [Fact]
    public void AnalyzeCoversEveryRecordedLine()
    {
        var then = new AutomationEngine(new VariableStore());
        var recording = Record(then, "line one", "line two", "line three");
        var now = new AutomationEngine(new VariableStore());

        var all = ReplayAnalyzer.Analyze(recording, now);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "line one", "line two", "line three" }, all.Select(a => a.Line));
    }

    [Fact]
    public void InputBreaksLineTriggerAssociation()
    {
        // A trigger match appearing after an input must not be attributed to a prior line.
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int i = 0;
        var bus = new EventBus { Clock = () => t.AddMilliseconds(100 * i++) };
        var rec = new SessionRecorder("W", t);
        bus.Subscribe(rec);
        bus.Emit(SessionEventKind.LineReceived, "some line");
        bus.Emit(SessionEventKind.InputSubmitted, "kill orc");
        bus.Emit(SessionEventKind.TriggerMatched, "kill orc", "strayTrigger", "send: x");
        var recording = new SessionRecording(rec.Header, rec.Events.ToArray());

        var now = new AutomationEngine(new VariableStore());
        var all = ReplayAnalyzer.Analyze(recording, now);
        ReplayLineAnalysis line = Assert.Single(all);
        Assert.Empty(line.FiredThen);   // the stray match was not attributed to "some line"
    }
}
