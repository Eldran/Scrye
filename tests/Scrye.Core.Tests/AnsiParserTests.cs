using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

public class AnsiParserTests
{
    private static List<Line> Parse(string s)
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch);
        var lines = new List<Line>();
        parser.LineCompleted += lines.Add;
        parser.Feed(s);
        parser.FlushAsPrompt();
        return lines;
    }

    [Fact]
    public void SplitsOnNewline()
    {
        var lines = Parse("alpha\nbeta\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("alpha", lines[0].PlainText);
        Assert.Equal("beta", lines[1].PlainText);
    }

    [Fact]
    public void IgnoresCarriageReturn()
    {
        var lines = Parse("hi\r\n");
        Assert.Equal("hi", lines[0].PlainText);
    }

    [Fact]
    public void ParsesBoldGreen()
    {
        var run = Parse("\x1b[1;32mHi\x1b[0m\n")[0].Runs[0];
        Assert.Equal("Hi", run.Text);
        Assert.True((run.Flags & RunFlags.Bold) != 0);
        Assert.Equal(Rgb.Ansi16(2, bright: true), run.Fore);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var runs = Parse("\x1b[31mred\x1b[0m plain\n")[0].Runs;
        Assert.Equal("red", runs[0].Text);
        Assert.Equal(" plain", runs[1].Text);
        Assert.Equal(Rgb.DefaultFore, runs[1].Fore);
    }

    [Fact]
    public void Parses256Colour()
    {
        var run = Parse("\x1b[38;5;208mx\n")[0].Runs[0];
        Assert.Equal(new Rgb(0xFF, 0x87, 0x00), run.Fore);
    }

    [Fact]
    public void ParsesTrueColour()
    {
        var run = Parse("\x1b[38;2;10;20;250mx\n")[0].Runs[0];
        Assert.Equal(new Rgb(10, 20, 250), run.Fore);
    }

    [Fact]
    public void BareTextFlushesAsPrompt()
    {
        var lines = Parse("Enter command> ");
        Assert.Single(lines);
        Assert.True(lines[0].IsPrompt);
        Assert.Equal("Enter command> ", lines[0].PlainText);
    }
}
