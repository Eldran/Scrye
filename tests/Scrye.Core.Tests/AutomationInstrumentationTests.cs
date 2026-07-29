using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class AutomationInstrumentationTests
{
    // A no-op IWorldActions that records what the engine asked it to do.
    private sealed class Spy : IWorldActions
    {
        private readonly VariableStore _vars;
        public Spy(VariableStore vars) => _vars = vars;
        public List<string> Sends { get; } = new();
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) { }
        public string? GetVariable(string name) => _vars.Get(name);
        public void SetVariable(string name, string value) => _vars.Set(name, value);
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    [Fact]
    public void TriggerHitReportsNameKindAndAction()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low on health*", Send = "flee" });
        AutomationHit? hit = null;
        engine.Hit += h => hit = h;

        engine.ProcessLine("You are low on health", new Spy(new VariableStore()));

        Assert.NotNull(hit);
        Assert.Equal(AutomationHitKind.Trigger, hit!.Value.Kind);
        Assert.Equal("flee", hit.Value.Name);
        Assert.Equal("send: flee", hit.Value.Action);
    }

    [Fact]
    public void AliasHitAndTimerHitFire()
    {
        var vars = new VariableStore();
        var engine = new AutomationEngine(vars);
        engine.AddAlias(new AliasDef { Name = "atk", Pattern = "kk *", Send = "kill %1" });
        engine.AddTimer(new TimerDef { Name = "save", IntervalSeconds = 5, Send = "save" });
        var hits = new List<AutomationHit>();
        engine.Hit += hits.Add;
        var spy = new Spy(vars);

        engine.ProcessInput("kk orc", spy);
        engine.Tick(5.0, spy);

        Assert.Equal(2, hits.Count);
        Assert.Equal(AutomationHitKind.Alias, hits[0].Kind);
        Assert.Equal("kill orc", ExtractSend(hits[0].Action));
        Assert.Equal(AutomationHitKind.Timer, hits[1].Kind);
    }

    [Fact]
    public void SimulateReportsHitsWithNoSideEffects()
    {
        var vars = new VariableStore();
        var engine = new AutomationEngine(vars);
        engine.AddTrigger(new TriggerDef { Name = "hp", IsRegex = true, Pattern = @"^HP:\s*(\d+)", SendTo = SendTo.Variable, Variable = "hp", Send = "%1" });
        engine.AddTrigger(new TriggerDef { Name = "oneshot", Pattern = "*boom*", Send = "duck", OneShot = true });

        var hits = engine.Simulate("HP: 42 boom");

        // 'hp' matches; 'oneshot' does NOT run because 'hp' isn't KeepEvaluating -> break.
        Assert.Single(hits);
        Assert.Equal("hp", hits[0].Name);
        Assert.Equal("var hp=42", hits[0].Action);
        // no side effects:
        Assert.Null(vars.Get("hp"));
        Assert.Equal(2, engine.TriggerCount);   // one-shot not consumed
    }

    [Fact]
    public void SimulateHonorsKeepEvaluatingAndDisabled()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "a", Pattern = "*hit*", Send = "one", KeepEvaluating = true, Sequence = 1 });
        engine.AddTrigger(new TriggerDef { Name = "b", Pattern = "*hit*", Send = "two", Sequence = 2 });
        engine.AddTrigger(new TriggerDef { Name = "c", Pattern = "*hit*", Send = "three", Sequence = 3, Enabled = false });

        var hits = engine.Simulate("a hit lands");

        // a (keep-evaluating) + b (stops), c disabled and never reached
        Assert.Equal(new[] { "a", "b" }, hits.Select(h => h.Name).ToArray());
    }

    private static string ExtractSend(string action) =>
        action.StartsWith("send: ") ? action["send: ".Length..] : action;
}
