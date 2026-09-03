using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class AutomationEngineTests
{
    private sealed class Recorder : IWorldActions
    {
        private readonly VariableStore _vars;
        public Recorder(VariableStore vars) => _vars = vars;
        public List<string> Sends { get; } = new();
        public List<string> Echoes { get; } = new();
        public List<(string fn, IReadOnlyList<string> wc)> Scripts { get; } = new();
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) => Echoes.Add(text);
        public string? GetVariable(string name) => _vars.Get(name);
        public void SetVariable(string name, string value) => _vars.Set(name, value);
        public void CallScript(string function, IReadOnlyList<string> wildcards) => Scripts.Add((function, wildcards));
    }

    private static (AutomationEngine engine, Recorder rec, VariableStore vars) NewEngine()
    {
        var vars = new VariableStore();
        return (new AutomationEngine(vars), new Recorder(vars), vars);
    }

    [Fact]
    public void AliasExpandsWildcard()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "atk", Pattern = "kk *", Send = "kill %1" });

        bool consumed = engine.ProcessInput("kk orc", rec);

        Assert.True(consumed);
        Assert.Equal(new[] { "kill orc" }, rec.Sends);
    }

    [Fact]
    public void AnEmptyPatternNeverMatches()
    {
        // The editor seeds a new alias with a name and a blank pattern. Saved that way with
        // Regex ticked, "" as a regex matched EVERY command - the alias fired on anything you
        // typed, including its own name, which is how the bug was reported. As a wildcard the
        // blank pattern matched an empty command instead. Neither is a match anyone asked for.
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "heal", Pattern = "", IsRegex = true, Send = "cast heal" });
        Assert.False(engine.ProcessInput("heal", rec));
        Assert.False(engine.ProcessInput("look", rec));
        engine.ClearAliases();
        engine.AddAlias(new AliasDef { Name = "heal", Pattern = "", IsRegex = false, Send = "cast heal" });
        Assert.False(engine.ProcessInput("", rec));
        Assert.False(engine.ProcessInput("heal", rec));
        Assert.Empty(rec.Sends);

        // and the same for a trigger: a blank regex trigger would otherwise fire on every line
        engine.AddTrigger(new TriggerDef { Name = "blank", Pattern = "", IsRegex = true, Send = "x" });
        engine.ProcessLine("anything at all", rec);
        Assert.Empty(rec.Sends);
    }

    [Fact]
    public void TheAliasNameIsNotAPattern()
    {
        // Only the pattern is ever matched; the name is a label. Typing the name of an alias
        // whose pattern is something else sends the name to the MUD like any other command.
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "heal", Pattern = "hh", Send = "cast heal" });
        Assert.False(engine.ProcessInput("heal", rec));
        Assert.True(engine.ProcessInput("hh", rec));
        Assert.Equal(new[] { "cast heal" }, rec.Sends);
    }

    [Fact]
    public void UnmatchedInputPassesThrough()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "atk", Pattern = "kk *", Send = "kill %1" });

        Assert.False(engine.ProcessInput("look", rec));
        Assert.Empty(rec.Sends);
    }

    [Fact]
    public void RegexTriggerStoresVariable()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef
        {
            Name = "hp", IsRegex = true, Pattern = @"^HP:\s*(\d+)/(\d+)",
            SendTo = SendTo.Variable, Variable = "hp", Send = "%1"
        });

        engine.ProcessLine("HP: 42/100 MP: 10/10", rec);

        Assert.Equal("42", vars.Get("hp"));
    }

    [Fact]
    public void OneShotTriggerFiresOnce()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "greet", Pattern = "* says hello", Send = "wave", OneShot = true });

        engine.ProcessLine("Bob says hello", rec);
        engine.ProcessLine("Bob says hello", rec);

        Assert.Equal(new[] { "wave" }, rec.Sends);
        Assert.Equal(0, engine.TriggerCount);
    }

    [Fact]
    public void ScriptTriggerPassesWildcards()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "tell", Pattern = "* tells you *", SendTo = SendTo.Script, Script = "onTell" });

        engine.ProcessLine("Alice tells you hi", rec);

        var (fn, wc) = Assert.Single(rec.Scripts);
        Assert.Equal("onTell", fn);
        Assert.Equal(new[] { "Alice", "hi" }, wc);
    }

    [Fact]
    public void SequenceOrdersTriggers()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "b", Pattern = "*", Send = "second", Sequence = 200, KeepEvaluating = true });
        engine.AddTrigger(new TriggerDef { Name = "a", Pattern = "*", Send = "first", Sequence = 100, KeepEvaluating = true });

        engine.ProcessLine("anything", rec);

        Assert.Equal(new[] { "first", "second" }, rec.Sends);
    }

    [Fact]
    public void KeepEvaluatingFalseStops()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "a", Pattern = "*", Send = "one", Sequence = 1 }); // KeepEvaluating default false
        engine.AddTrigger(new TriggerDef { Name = "b", Pattern = "*", Send = "two", Sequence = 2 });

        engine.ProcessLine("x", rec);

        Assert.Equal(new[] { "one" }, rec.Sends);
    }

    [Fact]
    public void DisabledTriggerDoesNotFire()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "*", Send = "x" });
        engine.EnableTrigger("t", false);

        engine.ProcessLine("hello", rec);

        Assert.Empty(rec.Sends);
    }

    [Fact]
    public void GroupToggleAffectsMembers()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t1", Pattern = "*", Send = "a", Group = "combat", KeepEvaluating = true });
        engine.AddTrigger(new TriggerDef { Name = "t2", Pattern = "*", Send = "b", Group = "combat", KeepEvaluating = true });
        engine.EnableTriggerGroup("combat", false);

        engine.ProcessLine("x", rec);

        Assert.Empty(rec.Sends);
    }

    [Fact]
    public void TimerFiresOnInterval()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTimer(new TimerDef { Name = "hb", IntervalSeconds = 5, Send = "save" });

        engine.Tick(3.0, rec);
        Assert.Empty(rec.Sends);      // not due yet
        engine.Tick(3.0, rec);
        Assert.Equal(new[] { "save" }, rec.Sends);   // 6s total -> fired
    }

    [Fact]
    public void AddingSameNameReplaces()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "*", Send = "old" });
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "*", Send = "new" });

        Assert.Equal(1, engine.TriggerCount);
        engine.ProcessLine("x", rec);
        Assert.Equal(new[] { "new" }, rec.Sends);
    }

    [Fact]
    public void VariableExpansionInTemplate()
    {
        var (engine, rec, vars) = NewEngine();
        vars.Set("target", "goblin");
        engine.AddAlias(new AliasDef { Name = "a", Pattern = "atk", Send = "kill ${target}" });

        engine.ProcessInput("atk", rec);

        Assert.Equal(new[] { "kill goblin" }, rec.Sends);
    }

    [Fact]
    public void MultiLineSendFiresOneCommandPerLine()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef
        {
            Name = "loot", Pattern = "* is dead*",
            Send = "get all from corpse\nbury corpse\n\nsay victory",   // blank line skipped
        });

        engine.ProcessLine("The orc is dead!", rec);

        Assert.Equal(new[] { "get all from corpse", "bury corpse", "say victory" }, rec.Sends);
    }

    [Fact]
    public void MultiLineSendExpandsWildcardsOnEveryLine()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "hunt", Pattern = "hunt *", Send = "track %1\r\nkill %1" });

        engine.ProcessInput("hunt bear", rec);

        Assert.Equal(new[] { "track bear", "kill bear" }, rec.Sends);
    }

    [Fact]
    public void SingleLineSendIsUnchanged()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "l", Pattern = "peek", Send = "look" });

        engine.ProcessInput("peek", rec);

        Assert.Equal(new[] { "look" }, rec.Sends);
    }
}
