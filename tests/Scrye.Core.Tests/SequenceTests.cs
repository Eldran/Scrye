using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class SequenceParserTests
{
    [Fact]
    public void ParsesSendRepeatAndWait()
    {
        SequenceDef d = SequenceParser.Parse("t", "enter; north x3; wait 2; west*2");
        Assert.Equal(4, d.Steps.Count);
        Assert.Equal("send", d.Steps[0].Kind); Assert.Equal("enter", d.Steps[0].Text); Assert.Equal(1, d.Steps[0].Count);
        Assert.Equal(3, d.Steps[1].Count);
        Assert.Equal("wait", d.Steps[2].Kind); Assert.Equal(2, d.Steps[2].Seconds);
        Assert.Equal("west", d.Steps[3].Text); Assert.Equal(2, d.Steps[3].Count);
    }

    [Fact]
    public void SplitsOnSemicolonsAndNewlines()
    {
        SequenceDef d = SequenceParser.Parse("t", "n\ns; e");
        Assert.Equal(new[] { "n", "s", "e" }, d.Steps.Select(x => x.Text));
    }
}

public class SequenceEngineTests
{
    private static (SequenceEngine engine, List<string> sent) NewEngine()
    {
        var e = new SequenceEngine();
        var sent = new List<string>();
        e.Send += sent.Add;
        return (e, sent);
    }

    [Fact]
    public void PromptGatedAdvancesOnEachPrompt()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(SequenceParser.Parse("t", "a; b; c"));
        Assert.Equal(new[] { "a" }, sent);            // first sent immediately
        e.OnPrompt();
        e.OnPrompt();
        Assert.Equal(new[] { "a", "b", "c" }, sent);  // one per prompt
        Assert.Equal(SequenceState.Finished, e.State);
    }

    [Fact]
    public void RepeatExpandsToMultipleSends()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(SequenceParser.Parse("t", "kick x3"));
        e.OnPrompt(); e.OnPrompt();
        Assert.Equal(new[] { "kick", "kick", "kick" }, sent);
    }

    [Fact]
    public void WaitStepAdvancesByTicks()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(SequenceParser.Parse("t", "a; wait 2; b"));
        e.OnPrompt();                 // a -> reaches wait step
        Assert.Equal(new[] { "a" }, sent);
        e.Tick(1.0);
        Assert.Equal(new[] { "a" }, sent);   // not yet
        e.Tick(1.0);                  // 2s elapsed -> b
        Assert.Equal(new[] { "a", "b" }, sent);
    }

    [Fact]
    public void PromptTimeoutAdvancesWhenNoPromptArrives()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(new SequenceDef { Name = "t", PromptGated = true, StepTimeoutSeconds = 2,
            Steps = new[] { SequenceStep.Send("a"), SequenceStep.Send("b") } });
        Assert.Equal(new[] { "a" }, sent);
        e.Tick(1.0); e.Tick(1.0);     // no prompt, timeout hits
        Assert.Equal(new[] { "a", "b" }, sent);
    }

    [Fact]
    public void PauseFreezesAndResumeContinues()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(SequenceParser.Parse("t", "a; b"));
        e.Pause();
        e.OnPrompt();                 // ignored while paused
        e.Tick(10.0);                 // ignored while paused
        Assert.Equal(new[] { "a" }, sent);
        e.Resume();
        e.OnPrompt();
        Assert.Equal(new[] { "a", "b" }, sent);
    }

    [Fact]
    public void StopHalts()
    {
        var (e, sent) = NewEngine();
        e.RunAdHoc(SequenceParser.Parse("t", "a; b; c"));
        e.OnPrompt();                 // b
        e.Stop();
        e.OnPrompt();                 // ignored
        Assert.Equal(new[] { "a", "b" }, sent);
        Assert.Equal(SequenceState.Stopped, e.State);
    }

    [Fact]
    public void RunByNameUsesRegistry()
    {
        var (e, sent) = NewEngine();
        e.Register(SequenceParser.Parse("home", "recall; look"));
        Assert.False(e.Run("nope"));
        Assert.True(e.Run("home"));
        Assert.Equal(new[] { "recall" }, sent);
    }

    [Fact]
    public void StatusReportsProgress()
    {
        var (e, _) = NewEngine();
        SequenceStatus last = default;
        e.StatusChanged += s => last = s;
        e.RunAdHoc(SequenceParser.Parse("walk", "n; s; e"));
        Assert.Equal(1, last.Sent);
        Assert.Equal(3, last.Total);
        Assert.True(last.Active);
        Assert.Equal("n", last.Command);
    }
}
