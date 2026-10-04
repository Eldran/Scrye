using System.Collections.Generic;
using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// "Repeat on line" (Joakim, 2 Oct 2026: "can we have it so a trigger may fire twice per line
/// as a setting?"): a trigger fires once for every match on the line, each with its own
/// wildcards - MUSHclient's "repeat on same line", imported from its repeat="y".
/// </summary>
public class TriggerRepeatTests
{
    private sealed class Rec : IWorldActions
    {
        public List<string> Sends { get; } = new();
        public List<(int Start, int Length)> Painted { get; } = new();
        public int Captures, Gags, Notifies, Sounds;
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
        public void Capture(string pane) => Captures++;
        public void GagLine() => Gags++;
        public void Notify() => Notifies++;
        public void PlaySound(string sound) => Sounds++;
        public void Highlight(Scrye.Core.Text.Rgb? fore, Scrye.Core.Text.Rgb? back, int start, int length) =>
            Painted.Add((start, length));
    }

    private static TriggerDef Hits(bool repeat) => new()
    {
        Name = "hits", Pattern = @"(\w+) hits you", IsRegex = true, Send = "say ouch %1", RepeatOnLine = repeat,
    };

    [Fact]
    public void ARepeatingTriggerFiresForEveryMatchWithItsOwnWildcards()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true));
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you. Bob hits you.", rec);
        Assert.Equal(new[] { "say ouch Bob", "say ouch Ann", "say ouch Bob" }, rec.Sends);
    }

    [Fact]
    public void WithoutTheSettingItFiresOnce()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: false));
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        Assert.Equal(new[] { "say ouch Bob" }, rec.Sends);
    }

    [Fact]
    public void TheLineItselfIsCapturedGaggedNotifiedAndSoundedOnce()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true) with { CapturePane = "Fights", Gag = true, Notify = true, Sound = "beep" });
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        Assert.Equal(2, rec.Sends.Count);
        Assert.Equal((1, 1, 1, 1), (rec.Captures, rec.Gags, rec.Notifies, rec.Sounds));
    }

    [Fact]
    public void EachMatchIsHighlighted()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true) with { HighlightFore = "#FF0000", HighlightWholeLine = false });
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        Assert.Equal(new[] { (0, 12), (14, 12) }, rec.Painted);
    }

    [Fact]
    public void AOneShotIsSpentOnItsFirstMatch()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true) with { OneShot = true });
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        engine.ProcessLine("Cid hits you.", rec);
        Assert.Equal(new[] { "say ouch Bob" }, rec.Sends);
    }

    [Fact]
    public void RepeatsAreCapped()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "x", Pattern = "x", IsRegex = true, Send = "hit", RepeatOnLine = true });
        var rec = new Rec();
        engine.ProcessLine(new string('x', 500), rec);
        Assert.Equal(TriggerDef.MaxRepeats, rec.Sends.Count);
    }

    [Fact]
    public void APatternThatCanMatchNothingFiresOnce()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "z", Pattern = "z*", IsRegex = true, Send = "hit", RepeatOnLine = true });
        var rec = new Rec();
        engine.ProcessLine("abc", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void AWildcardPatternSpansTheLineSoItMatchesOnce()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "w", Pattern = "* hits you*", Send = "ouch", RepeatOnLine = true });
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void ATriggerOverSeveralLinesFiresOnce()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true) with { Lines = 2 });
        var rec = new Rec();
        engine.ProcessLine("Bob hits you. Ann hits you.", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void TheSimulationCountsEveryMatch()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(Hits(repeat: true));
        Assert.Equal(2, engine.Simulate("Bob hits you. Ann hits you.").Count);
    }

    [Fact]
    public void MushclientRepeatImports()
    {
        MushclientImport imp = MushclientImport.Parse(
            "<muclient><triggers><trigger name=\"a\" match=\"(\\w+) hits you\" regexp=\"y\" repeat=\"y\" enabled=\"y\" send_to=\"0\"><send>x</send></trigger>"
            + "<trigger name=\"b\" match=\"foo\" enabled=\"y\" send_to=\"0\"><send>y</send></trigger></triggers></muclient>", "m");
        Assert.True(imp.Triggers[0].RepeatOnLine);
        Assert.False(imp.Triggers[1].RepeatOnLine);
    }
}
