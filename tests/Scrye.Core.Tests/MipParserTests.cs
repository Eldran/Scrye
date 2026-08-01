using Scrye.Core.Mip;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>Packet-level MIP frame extraction, focused on the split/terminator edge
/// cases that previously leaked raw frames into the output window.</summary>
public sealed class MipParserTests
{
    private const string Frame =
        "#K%70695150BBEWSTOCK_2of3^^bread|176|100|hardtack;amber|3|100|tools|494|100;fine";

    private static (string Displayed, List<MipMessage> Frames) Run(params string[] packets)
    {
        var parser = new MipParser();
        var frames = new List<MipMessage>();
        parser.MessageReceived += m => frames.Add(m);
        var sb = new System.Text.StringBuilder();
        foreach (string p in packets) sb.Append(parser.Process(p));
        return (sb.ToString(), frames);
    }

    [Fact]
    public void CompleteCrlfFrameIsConsumed()
    {
        (string displayed, var frames) = Run(Frame + "\r\n");
        Assert.Equal("", displayed);
        MipMessage m = Assert.Single(frames);
        Assert.Equal("70695", m.Id);
        Assert.Equal("BBE", m.Tag);
        Assert.StartsWith("WSTOCK_2of3^^", m.Data);
    }

    [Fact]
    public void PacketSplitBetweenHashAndKIsHeldNotLeaked()
    {
        int hash = Frame.IndexOf("#K%", StringComparison.Ordinal);
        (string displayed, var frames) = Run("hello\r\n" + Frame[..(hash + 1)], Frame[(hash + 1)..] + "\r\n");
        Assert.Equal("hello\r\n", displayed);
        Assert.Single(frames);
    }

    [Fact]
    public void CarriageReturnOnlyTerminatedFramesAreConsumed()
    {
        // the server terminates some MIP bursts with bare \r — two frames back to back
        (string displayed, var frames) = Run(Frame + "\r" + Frame + "\r", "next line\r\n");
        Assert.Equal("next line\r\n", displayed);
        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void FrameSplitAcrossThreePacketsIsReassembled()
    {
        (string displayed, var frames) = Run(Frame[..30], Frame[30..60], Frame[60..] + "\r\n");
        Assert.Equal("", displayed);
        Assert.Single(frames);
    }

    [Fact]
    public void TrailingCrHeldUntilTheLfArrives()
    {
        (string displayed, var frames) = Run(Frame + "\r", "\n");
        Assert.Equal("", displayed);
        Assert.Single(frames);
    }

    [Fact]
    public void NormalTextWithBareCrPassesThroughUnchanged()
    {
        (string displayed, var frames) = Run("abc\rdef\r\n");
        Assert.Equal("abc\rdef\r\n", displayed);
        Assert.Empty(frames);
    }

    [Fact]
    public void PromptWithoutNewlineIsFlushedImmediately()
    {
        (string displayed, var frames) = Run("> ");
        Assert.Equal("> ", displayed);
        Assert.Empty(frames);
    }

    [Fact]
    public void TextPrecedingTheMarkerIsKept()
    {
        (string displayed, var frames) = Run("tail-of-line" + Frame + "\r\n");
        Assert.Equal("tail-of-line\r\n", displayed);
        Assert.Single(frames);
    }

    [Fact]
    public void DisplayTimeFallbackConsumesALeakedFrame()
    {
        var parser = new MipParser();
        var frames = new List<MipMessage>();
        parser.MessageReceived += m => frames.Add(m);

        Assert.True(parser.TryConsumeDisplayedLine(Frame, out string pre));
        Assert.Equal("", pre);
        Assert.Single(frames);

        Assert.False(parser.TryConsumeDisplayedLine("no marker here", out _));
        Assert.False(parser.TryConsumeDisplayedLine("#K% but not a frame", out _));
    }
}
