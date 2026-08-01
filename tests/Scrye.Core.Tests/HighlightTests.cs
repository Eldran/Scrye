using Scrye.Core.Automation;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

public class HighlightTests
{
    private static Line MakeLine(params (string text, Rgb fore)[] runs) =>
        new(runs.Select(r => new StyledRun(r.text, r.fore, Rgb.DefaultBack, RunFlags.None)).ToArray(),
            false, DateTimeOffset.UtcNow);

    private static readonly Rgb Red = new(0xFF, 0x00, 0x00);
    private static readonly Rgb Grey = Rgb.DefaultFore;

    // ---- Rgb hex ----

    [Theory]
    [InlineData("#FF8800", 0xFF, 0x88, 0x00)]
    [InlineData("ff8800", 0xFF, 0x88, 0x00)]
    [InlineData("  #00FF10 ", 0x00, 0xFF, 0x10)]
    public void ParsesHex(string s, int r, int g, int b)
    {
        Assert.True(Rgb.TryParseHex(s, out Rgb c));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("nothex")]
    [InlineData("#GGGGGG")]
    public void RejectsBadHex(string? s) => Assert.False(Rgb.TryParseHex(s, out _));

    [Fact]
    public void HexRoundTrips()
    {
        var c = new Rgb(0x35, 0xC4, 0xD6);
        Assert.Equal("#35C4D6", c.ToHex());
        Assert.True(Rgb.TryParseHex(c.ToHex(), out Rgb back));
        Assert.Equal(c, back);
    }

    // ---- Line.RecolorRange ----

    [Fact]
    public void RecolorWholeSingleRun()
    {
        Line line = MakeLine(("hello world", Grey));
        Line hl = line.RecolorRange(0, 11, Red, null);
        Assert.Equal("hello world", hl.PlainText);
        Assert.Single(hl.Runs);
        Assert.Equal(Red, hl.Runs[0].Fore);
    }

    [Fact]
    public void RecolorSubstringSplitsRun()
    {
        Line line = MakeLine(("the orc dies", Grey));
        Line hl = line.RecolorRange(4, 3, Red, null);   // "orc"
        Assert.Equal("the orc dies", hl.PlainText);
        Assert.Equal(3, hl.Runs.Count);
        Assert.Equal("the ", hl.Runs[0].Text); Assert.Equal(Grey, hl.Runs[0].Fore);
        Assert.Equal("orc", hl.Runs[1].Text);  Assert.Equal(Red, hl.Runs[1].Fore);
        Assert.Equal(" dies", hl.Runs[2].Text); Assert.Equal(Grey, hl.Runs[2].Fore);
    }

    [Fact]
    public void RecolorSpanningMultipleRunsRecolorsEach()
    {
        Line line = MakeLine(("foo", Grey), ("bar", Red), ("baz", Grey));  // "foobarbaz"
        Line hl = line.RecolorRange(2, 5, new Rgb(1, 2, 3), null);         // "obarb"
        Assert.Equal("foobarbaz", hl.PlainText);
        // every recoloured piece carries the new fore
        Rgb want = new(1, 2, 3);
        string coloured = string.Concat(hl.Runs.Where(r => r.Fore.Equals(want)).Select(r => r.Text));
        Assert.Equal("obarb", coloured);
    }

    [Fact]
    public void RecolorPreservesFlagsAndLink()
    {
        var link = new LinkInfo("north", IsUrl: false);
        var line = new Line(new[] { new StyledRun("go north now", Grey, Rgb.DefaultBack, RunFlags.Bold, link) },
                            false, DateTimeOffset.UtcNow);
        Line hl = line.RecolorRange(3, 5, Red, null);   // "north"
        foreach (StyledRun r in hl.Runs)
        {
            Assert.Equal(RunFlags.Bold, r.Flags);
            Assert.Same(link, r.Link);
        }
    }

    [Fact]
    public void RecolorBackgroundOnlyKeepsFore()
    {
        Line line = MakeLine(("alert", Red));
        var yellow = new Rgb(0xFF, 0xFF, 0x00);
        Line hl = line.RecolorRange(0, 5, null, yellow);
        Assert.Equal(Red, hl.Runs[0].Fore);       // fore untouched
        Assert.Equal(yellow, hl.Runs[0].Back);
    }

    [Fact]
    public void RecolorClampsAndNoOps()
    {
        Line line = MakeLine(("hi", Grey));
        Assert.Same(line, line.RecolorRange(0, 0, Red, null));     // empty
        Assert.Same(line, line.RecolorRange(5, 3, Red, null));     // past end
        Assert.Same(line, line.RecolorRange(0, 2, null, null));    // no colour
        Line clamped = line.RecolorRange(-3, 10, Red, null);       // over-wide clamps to the text
        Assert.Equal("hi", clamped.PlainText);
        Assert.All(clamped.Runs, r => Assert.Equal(Red, r.Fore));
    }

    // ---- end-to-end through the engine ----

    private sealed class HiRecorder : IWorldActions
    {
        public List<(Rgb? fore, Rgb? back, int start, int len)> Highlights { get; } = new();
        public void Send(string text) { }
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
        public void Highlight(Rgb? fore, Rgb? back, int start, int length) => Highlights.Add((fore, back, start, length));
    }

    [Fact]
    public void WholeLineHighlightTriggerSpansLine()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef
        {
            Name = "bleed", Pattern = "*bleeding*",
            HighlightFore = "#FF0000", HighlightWholeLine = true,
        });
        var rec = new HiRecorder();
        engine.ProcessLine("you are bleeding badly", rec);

        var h = Assert.Single(rec.Highlights);
        Assert.Equal(new Rgb(0xFF, 0, 0), h.fore);
        Assert.Null(h.back);
        Assert.Equal(0, h.start);
        Assert.Equal("you are bleeding badly".Length, h.len);
    }

    [Fact]
    public void MatchOnlyHighlightTriggerSpansMatch()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef
        {
            Name = "orc", IsRegex = true, Pattern = "orc",
            HighlightFore = "#00FF00", HighlightWholeLine = false,
        });
        var rec = new HiRecorder();
        engine.ProcessLine("the orc snarls", rec);

        var h = Assert.Single(rec.Highlights);
        Assert.Equal(4, h.start);
        Assert.Equal(3, h.len);
    }

    [Fact]
    public void NoHighlightWhenColorUnset()
    {
        var engine = new AutomationEngine(new VariableStore());
        engine.AddTrigger(new TriggerDef { Name = "x", Pattern = "*hi*", Send = "wave" });
        var rec = new HiRecorder();
        engine.ProcessLine("hi there", rec);
        Assert.Empty(rec.Highlights);
    }
}
