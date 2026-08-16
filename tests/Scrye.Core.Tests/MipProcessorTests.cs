using Scrye.Core.Automation;
using Scrye.Core.Mip;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>BBE viking-feed decoding, including the chunked KEY_&lt;n&gt;of&lt;m&gt; format
/// (matches the reference ThreeS_MIP plugin behaviour).</summary>
public sealed class MipProcessorTests
{
    private static (MipProcessor proc, VariableStore vars, List<(string k, string v)> events) Make()
    {
        var vars = new VariableStore();
        var proc = new MipProcessor(vars);
        var events = new List<(string, string)>();
        proc.VikingData += (k, v) => events.Add((k, v));
        return (proc, vars, events);
    }

    private static MipMessage Bbe(string data) => new("12345", "BBE", data);

    [Fact]
    public void PlainKeysStillDecode()
    {
        (MipProcessor proc, VariableStore vars, var events) = Make();
        proc.Handle(Bbe("SEID^^12^^DALER^^3400"));
        Assert.Equal("12", vars.Get("vmip_SEID"));
        Assert.Equal("3400", vars.Get("vmip_DALER"));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ChunkedValueIsReassembledUnderTheBaseKey()
    {
        (MipProcessor proc, VariableStore vars, var events) = Make();
        proc.Handle(Bbe("SHIPS_1of3^^aaa"));
        proc.Handle(Bbe("SHIPS_2of3^^bbb"));
        Assert.Null(vars.Get("vmip_SHIPS"));            // incomplete: nothing published yet
        Assert.Empty(events);

        proc.Handle(Bbe("SHIPS_3of3^^ccc"));
        Assert.Equal("aaabbbccc", vars.Get("vmip_SHIPS"));
        Assert.Single(events);
        Assert.Equal(("SHIPS", "aaabbbccc"), events[0]);
        Assert.Null(vars.Get("vmip_SHIPS_1of3"));       // chunk keys are never stored
    }

    [Fact]
    public void ChunksArriveInterleavedWithOtherKeys()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        proc.Handle(Bbe("SHIPS_1of2^^left^^SEID^^7^^SHIPS_2of2^^right"));
        Assert.Equal("leftright", vars.Get("vmip_SHIPS"));
        Assert.Equal("7", vars.Get("vmip_SEID"));
    }

    [Fact]
    public void ANewPartOneRestartsTheBuffer()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        proc.Handle(Bbe("SHIPS_1of2^^stale"));
        proc.Handle(Bbe("SHIPS_1of2^^fresh"));           // retransmission from the top
        proc.Handle(Bbe("SHIPS_2of2^^-tail"));
        Assert.Equal("fresh-tail", vars.Get("vmip_SHIPS"));
    }

    [Fact]
    public void DifferentChunkCountRestartsTheBuffer()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        proc.Handle(Bbe("SHIPS_1of3^^a"));
        proc.Handle(Bbe("SHIPS_1of2^^x"));               // new transmission, fewer chunks
        proc.Handle(Bbe("SHIPS_2of2^^y"));
        Assert.Equal("xy", vars.Get("vmip_SHIPS"));
    }

    [Fact]
    public void OldFormatNumericSuffixChunksAreIgnored()
    {
        (MipProcessor proc, VariableStore vars, var events) = Make();
        proc.Handle(Bbe("SHIPS_2^^junk"));
        Assert.Null(vars.Get("vmip_SHIPS_2"));
        Assert.Null(vars.Get("vmip_SHIPS"));
        Assert.Empty(events);
    }

    [Fact]
    public void OutOfRangeChunkIndicesAreIgnored()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        proc.Handle(Bbe("SHIPS_0of2^^bad"));             // n < 1: old-format-ish, ignored
        proc.Handle(Bbe("SHIPS_1of2^^a^^SHIPS_2of2^^b"));
        Assert.Equal("ab", vars.Get("vmip_SHIPS"));
    }

    [Fact]
    public void VmaphChangeStampsAFreshnessTime()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        Assert.Null(vars.Get("vmaph_time"));
        proc.Handle(Bbe("VMAPH^^row1row2"));
        string? stamp = vars.Get("vmaph_time");
        Assert.NotNull(stamp);
        Assert.True(long.TryParse(stamp, out long unix) && unix > 0);

        proc.Handle(Bbe("VMAPH^^row1row2"));             // unchanged value: stamp not rewritten
        Assert.Equal(stamp, vars.Get("vmaph_time"));
    }

    // ---- glines: both forms, for two different readers ------------------------------------
    //
    // 3Scapes colours a gline's labels and values differently, so the colour tags ARE its field
    // boundaries. Stripping them is right for display and destructive for a parser — and some
    // guilds (the elemental one, measured) send no BBE keys at all, so their glines are the only
    // thing a plugin has to work with.

    /// <summary>A real elemental gline, colour tags intact.</summary>
    private const string ColouredGline =
        "<yEmit> : <r16>  <gForm>: <cTime>(<r1550>)  <cRating>: <r745>";

    [Fact]
    public void AGlineIsPublishedStrippedForDisplayAndRawForParsing()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();

        proc.Handle(new MipMessage("12345", "FFF", $"I~{ColouredGline}"));

        Assert.Equal("Emit : 16  Form: Time(1550)  Rating: 745", vars.Get("gline1"));
        Assert.Equal(ColouredGline, vars.Get("gline1_raw"));
    }

    [Fact]
    public void TheRawGlineIsWhatMakesItsFieldsRecoverable()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();
        proc.Handle(new MipMessage("12345", "FFF", $"I~{ColouredGline}"));

        string[] values = System.Text.RegularExpressions.Regex
            .Matches(vars.Get("gline1_raw")!, "<r([^>]*)>")
            .Select(m => m.Groups[1].Value).ToArray();

        // The whole point: three unambiguous numbers, where the stripped form offers only
        // whitespace to guess at.
        Assert.Equal(new[] { "16", "1550", "745" }, values);
    }

    [Fact]
    public void AGlineWithNoColourTagsLosesNothing()
    {
        (MipProcessor proc, VariableStore vars, _) = Make();

        // The viking guild delimits with brackets instead, and sends no colour tags at all.
        proc.Handle(new MipMessage("12345", "FFF", "I~H[7044|7053] S[5319|5319]~J~L[4|4]"));

        Assert.Equal(vars.Get("gline1"), vars.Get("gline1_raw"));
        Assert.Equal(vars.Get("gline2"), vars.Get("gline2_raw"));
    }
}
