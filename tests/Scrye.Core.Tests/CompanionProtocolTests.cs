using Scrye.Companion.Protocol;
using Scrye.Core.State;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The companion wire contract (design doc §3). These tests exist mostly to pin down the
/// three corrections in §3.3 — the things an earlier draft of the protocol got wrong — so
/// nobody reintroduces them: colour is resolved RGB (not a palette index), a state value
/// keeps its kind (not a double), and max is a sibling path (not a field).
/// </summary>
public class CompanionProtocolTests
{
    private static readonly Rgb Bg = Rgb.DefaultBack;

    private static Line Styled(params StyledRun[] runs) =>
        new(runs, isPrompt: false, System.DateTimeOffset.UnixEpoch);

    // ---- style interning -----------------------------------------------------

    [Fact]
    public void RepeatedStyles_CollapseIntoOneTableEntry()
    {
        var red = new Rgb(0xAA, 0x00, 0x00);
        var white = new Rgb(0xFF, 0xFF, 0xFF);

        var a = Styled(
            new StyledRun("The wiremouth ", red, Bg, RunFlags.None),
            new StyledRun("attacks you!", white, Bg, RunFlags.Bold));
        var b = Styled(
            new StyledRun("You dodge. ", red, Bg, RunFlags.None),
            new StyledRun("Barely.", white, Bg, RunFlags.Bold));

        var builder = new OutputBatchBuilder();
        builder.AddRange(new[] { a, b }, firstSequence: 100);
        OutputBatchMessage msg = builder.Build("world");

        Assert.Equal(2, msg.Styles.Count);                  // four spans, two styles
        Assert.Equal(0, msg.Lines[1].Spans[0].StyleIndex);  // second line reuses them
        Assert.Equal(1, msg.Lines[1].Spans[1].StyleIndex);
    }

    [Fact]
    public void SequencesAreCarriedThrough()
    {
        var builder = new OutputBatchBuilder();
        builder.AddRange(
            new[] { Styled(new StyledRun("a", Rgb.DefaultFore, Bg, RunFlags.None)),
                    Styled(new StyledRun("b", Rgb.DefaultFore, Bg, RunFlags.None)) },
            firstSequence: 100);

        OutputBatchMessage msg = builder.Build("world");

        Assert.Equal(100, msg.Lines[0].Sequence);
        Assert.Equal(101, msg.Lines[1].Sequence);
    }

    [Fact]
    public void ColoursAreResolvedHex_NotPaletteIndices()
    {
        // AnsiParser resolves 16/256-colour codes to 24-bit Rgb and drops the index,
        // so there is no index to send — see §3.3.
        var builder = new OutputBatchBuilder();
        builder.Add(Styled(new StyledRun("x", Rgb.Ansi16(1, bright: false), Bg, RunFlags.None)), 1);

        Assert.Equal(Rgb.Ansi16(1, bright: false).ToHex(), builder.Build("w").Styles[0].Fg);
    }

    [Fact]
    public void AllRunFlags_Survive_NotJustBold()
    {
        var builder = new OutputBatchBuilder();
        builder.Add(
            Styled(new StyledRun("x", Rgb.DefaultFore, Bg, RunFlags.Underline | RunFlags.Italic)),
            1);

        Assert.Equal(RunFlags.Underline | RunFlags.Italic, builder.Build("w").Styles[0].Flags);
    }

    [Fact]
    public void PromptFlag_IsCarried()
    {
        var line = new Line(
            new[] { new StyledRun("HP:812> ", Rgb.DefaultFore, Bg, RunFlags.None) },
            isPrompt: true,
            System.DateTimeOffset.UnixEpoch);

        var builder = new OutputBatchBuilder();
        builder.Add(line, 1);

        Assert.True(builder.Build("w").Lines[0].IsPrompt);
    }

    [Fact]
    public void MxpLinks_CrossTheWire()
    {
        var link = new LinkInfo("kill wiremouth", IsUrl: false, Prompt: false, Hint: "attack it");
        var line = Styled(
            new StyledRun("wiremouth", Rgb.DefaultFore, Bg, RunFlags.None, link),
            new StyledRun(" is here.", Rgb.DefaultFore, Bg, RunFlags.None));

        var builder = new OutputBatchBuilder();
        builder.Add(line, 1);
        OutputBatchMessage msg = builder.Build("w");

        Assert.Equal("kill wiremouth", msg.Lines[0].Spans[0].Link!.Action);
        Assert.Equal("attack it", msg.Lines[0].Spans[0].Link!.Hint);
        Assert.Null(msg.Lines[0].Spans[1].Link);
    }

    // ---- JSON ----------------------------------------------------------------

    [Fact]
    public void OutputBatch_RoundTrips()
    {
        var builder = new OutputBatchBuilder();
        builder.Add(
            Styled(new StyledRun("812", new Rgb(0xFF, 0xFF, 0x55), Bg, RunFlags.Bold)),
            18422);

        string json = CompanionJson.Serialize(builder.Build("threescapes-eldran"));
        OutputBatchMessage? back = CompanionJson.Deserialize<OutputBatchMessage>(json);

        Assert.NotNull(back);
        Assert.Equal("threescapes-eldran", back!.SessionId);
        Assert.Equal(18422, back.Lines[0].Sequence);
        Assert.Equal("#FFFF55", back.Styles[0].Fg);
        Assert.Equal(RunFlags.Bold, back.Styles[0].Flags);
    }

    [Fact]
    public void NullLinks_AreOmitted_NotSerializedAsNull()
    {
        // Most spans have no link; "link":null on every one of them across a combat
        // burst is pure waste (§3.1).
        var builder = new OutputBatchBuilder();
        builder.Add(Styled(new StyledRun("plain", Rgb.DefaultFore, Bg, RunFlags.None)), 1);

        Assert.DoesNotContain("\"link\":null", CompanionJson.Serialize(builder.Build("w")));
    }

    [Fact]
    public void PeekType_ReadsTheDiscriminatorWithoutFullDeserialization()
    {
        var builder = new OutputBatchBuilder();
        builder.Add(Styled(new StyledRun("x", Rgb.DefaultFore, Bg, RunFlags.None)), 1);

        Assert.Equal(MessageTypes.OutputBatch, CompanionJson.PeekType(CompanionJson.Serialize(builder.Build("w"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"nope\":1}")]
    public void PeekType_ReturnsNullOnJunk(string junk) =>
        Assert.Null(CompanionJson.PeekType(junk));

    [Fact]
    public void EnumsSerializeAsNames_SoAVersionSkewStaysReadable()
    {
        string json = CompanionJson.Serialize(
            new ErrorMessage(CompanionErrorCode.PermissionDenied, "scripting not permitted"));

        Assert.Contains("\"code\":\"permissionDenied\"", json);
    }

    // ---- state ---------------------------------------------------------------

    [Fact]
    public void StateUpdate_PreservesKind_IncludingStrings()
    {
        // Typing the wire value as a double would silently destroy every string leaf.
        var store = new StateStore();
        var seen = new List<StateUpdateMessage>();
        store.Changed += c => seen.Add(StateUpdateMessage.From("w", c));

        store.Set("char.name", StateValue.Str("Eldran"));
        store.Set("char.afk", StateValue.Boolean(false));
        store.Set("char.vitals.hp", StateValue.Num(812));

        Assert.Equal(StateKind.String, seen.Single(m => m.Path == "char.name").Kind);
        Assert.Equal("Eldran", seen.Single(m => m.Path == "char.name").Text);
        Assert.Equal(StateKind.Bool, seen.Single(m => m.Path == "char.afk").Kind);
        Assert.Equal(StateKind.Number, seen.Single(m => m.Path == "char.vitals.hp").Kind);
    }

    [Fact]
    public void MaxIsASiblingPath_NotAFieldOnTheValue()
    {
        var store = new StateStore();
        var seen = new List<StateUpdateMessage>();
        store.Changed += c => seen.Add(StateUpdateMessage.From("w", c));

        store.SetJson("char.vitals", "{\"hp\":812,\"maxhp\":1000}");

        Assert.Equal("812", seen.Single(m => m.Path == "char.vitals.hp").Text);
        Assert.Equal("1000", seen.Single(m => m.Path == "char.vitals.maxhp").Text);
    }

    [Fact]
    public void DroppedLeaf_IsReportedAsRemoved()
    {
        // A GMCP package resend that omits a key removes it; a client that ignored
        // this would keep showing a stale value forever.
        var store = new StateStore();
        store.SetJson("char.vitals", "{\"hp\":812,\"maxhp\":1000}");

        var seen = new List<StateUpdateMessage>();
        store.Changed += c => seen.Add(StateUpdateMessage.From("w", c));
        store.SetJson("char.vitals", "{\"hp\":900}");

        StateUpdateMessage removal = seen.Single(m => m.Path == "char.vitals.maxhp");
        Assert.True(removal.Removed);
    }

    [Fact]
    public void StateUpdate_RoundTrips()
    {
        var msg = new StateUpdateMessage("w", "char.name", StateKind.String, "Eldran");
        string json = CompanionJson.Serialize(msg);

        Assert.Contains("\"kind\":\"string\"", json);
        Assert.Equal(msg, CompanionJson.Deserialize<StateUpdateMessage>(json));
    }
}
