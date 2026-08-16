using Scrye.Core.Model;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// MXP: line-security modes, the link tags, server-set variables/gauges/destinations, and
/// custom element and entity definitions.
///
/// <para>The security cases are the point of this file. MXP is markup arriving from a remote
/// server, and the whole design rests on <b>secure mode</b>: a line the server has not marked
/// secure must not be able to create a clickable command, set a variable, define new vocabulary,
/// or redirect output. Most tests here assert something is <i>refused</i>.</para>
/// </summary>
public class MxpTests
{
    private const string Secure = "\x1b[1z";
    private const string Open = "\x1b[0z";
    private const string Locked = "\x1b[2z";
    private const string TempSecure = "\x1b[4z";

    private static (List<Line> Lines, List<string> Replies) Parse(string s, bool enabled = true)
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = enabled };
        var lines = new List<Line>();
        var replies = new List<string>();
        parser.LineCompleted += lines.Add;
        parser.MxpResponse += replies.Add;
        parser.Feed(s);
        return (lines, replies);
    }

    // ---- line security -------------------------------------------------------

    [Fact]
    public void SendCreatesALinkOnASecureLine()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND href=\"look sword\">a sword</SEND>\n");
        Assert.Equal("a sword", lines[0].PlainText);
        Assert.Single(lines[0].Links);
        Assert.Equal("look sword", lines[0].Links[0].Link.Action);
    }

    [Fact]
    public void SendIsInertOnAnOpenLine()
    {
        (List<Line> lines, _) = Parse("<SEND href=\"look sword\">a sword</SEND>\n");
        Assert.Equal("a sword", lines[0].PlainText);   // text still shows
        Assert.Empty(lines[0].Links);                  // but it is not clickable
    }

    [Fact]
    public void LockedModeDoesNotInterpretMarkupAtAll()
    {
        (List<Line> lines, _) = Parse(Locked + "<SEND href=\"x\">hi</SEND>\n");
        Assert.Contains("<SEND", lines[0].PlainText);
    }

    [Fact]
    public void TempSecureAppliesToExactlyOneTag()
    {
        (List<Line> lines, _) = Parse(TempSecure + "<SEND href=\"a\">one</SEND><SEND href=\"b\">two</SEND>\n");
        Assert.Single(lines[0].Links);
        Assert.Equal("a", lines[0].Links[0].Link.Action);
    }

    [Fact]
    public void SecurityModeResetsAtEachNewline()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND href=\"a\">one</SEND>\n<SEND href=\"b\">two</SEND>\n");
        Assert.Single(lines[0].Links);
        Assert.Empty(lines[1].Links);
    }

    // ---- SEND attributes -----------------------------------------------------

    [Fact]
    public void PositionalHrefAndHintWithTextSubstitution()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND \"kill &text;\" \"attack it\">troll</SEND>\n");
        LinkInfo link = lines[0].Links[0].Link;
        Assert.Equal("kill troll", link.Action);
        Assert.Equal("attack it", link.Hint);
    }

    [Fact]
    public void SendWithoutHrefSendsItsOwnText()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND>north</SEND>\n");
        Assert.Equal("north", lines[0].Links[0].Link.Action);
    }

    /// <summary>Regression: a valueless PROMPT arrives shaped exactly like a positional value,
    /// and used to be taken as the href — so the real command was silently replaced by the
    /// literal string "PROMPT".</summary>
    [Fact]
    public void BarePromptFlagDoesNotOverwriteTheHref()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND href=\"say \" PROMPT>say</SEND>\n");
        LinkInfo link = lines[0].Links[0].Link;
        Assert.Equal("say ", link.Action);   // including the trailing space
        Assert.True(link.Prompt);
    }

    /// <summary>Regression: attribute values were trimmed after unquoting, so a deliberate
    /// trailing space in a prompt prefix was lost.</summary>
    [Fact]
    public void QuotedAttributeKeepsSignificantWhitespace()
    {
        (List<Line> lines, _) = Parse(Secure + "<SEND href=\"tell bob \" PROMPT>bob</SEND>\n");
        Assert.Equal("tell bob ", lines[0].Links[0].Link.Action);
    }

    // ---- entities and malformed input ----------------------------------------

    [Fact]
    public void StandardEntitiesDecode()
    {
        (List<Line> lines, _) = Parse("5 &lt; 6 &amp;&amp; 7 &gt; 6&nbsp;ok\n");
        Assert.Equal("5 < 6 && 7 > 6 ok", lines[0].PlainText);
    }

    [Theory]
    [InlineData("a <notatag b\n", "<notatag")]
    [InlineData("&notanentity; done\n", "notanentity")]
    public void MalformedMarkupIsReplayedRatherThanEaten(string input, string expected)
    {
        (List<Line> lines, _) = Parse(input);
        Assert.Contains(expected, lines[0].PlainText);
    }

    [Fact]
    public void AnOversizedTagIsReplayedAsText()
    {
        (List<Line> lines, _) = Parse("x <" + new string('q', 600) + ">\n");
        Assert.StartsWith("x <", lines[0].PlainText);
    }

    [Fact]
    public void ATagSplitAcrossFeedsIsReassembled()
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = true };
        var lines = new List<Line>();
        parser.LineCompleted += lines.Add;
        parser.Feed(Secure + "<SEND href=\"go");
        parser.Feed(" north\">exit</SEND>\n");
        Assert.Equal("go north", Assert.Single(lines[0].Links).Link.Action);
    }

    [Fact]
    public void DisabledParserLeavesTagsAlone()
    {
        (List<Line> lines, _) = Parse(Secure + "<B>x</B>\n", enabled: false);
        Assert.Contains("<B>", lines[0].PlainText);
    }

    // ---- formatting ----------------------------------------------------------

    [Fact]
    public void StrikeoutOpensAndCloses()
    {
        (List<Line> lines, _) = Parse("<S>gone</S>here\n");
        Assert.Contains(lines[0].Runs, r => r.Text == "gone" && (r.Flags & RunFlags.Strikeout) != 0);
        Assert.Contains(lines[0].Runs, r => r.Text == "here" && (r.Flags & RunFlags.Strikeout) == 0);
    }

    /// <summary>The SUPPORT reply is a promise. It used to advertise +s while &lt;S&gt; was
    /// stripped, so a server could use strikeout to mean "destroyed" and the meaning vanished.</summary>
    [Fact]
    public void SupportAdvertisesOnlyWhatIsImplemented()
    {
        (_, List<string> replies) = Parse(Secure + "<SUPPORT>\n");
        string reply = Assert.Single(replies);
        Assert.Contains("+strikeout", reply);
        Assert.Contains("+var", reply);
        Assert.Contains("+dest", reply);
        Assert.Contains("+gauge", reply);
        Assert.DoesNotContain("+image", reply);
        Assert.DoesNotContain("+sound", reply);
    }

    [Fact]
    public void VersionIsAnswered()
    {
        (_, List<string> replies) = Parse(Secure + "<VERSION>\n");
        Assert.Contains("MXP=1.0", Assert.Single(replies));
    }

    // ---- VAR / DEST / GAUGE --------------------------------------------------

    [Fact]
    public void VarReportsNameAndValue()
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = true };
        var seen = new List<(string, string)>();
        parser.MxpVariable += (n, v) => seen.Add((n, v));
        parser.Feed(Secure + "<VAR roomname>Temple Square</VAR>\n");
        Assert.Equal(("roomname", "Temple Square"), Assert.Single(seen));
    }

    [Fact]
    public void VarIsRefusedOnAnOpenLine()
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = true };
        int fired = 0;
        parser.MxpVariable += (_, _) => fired++;
        parser.Feed("<VAR hp>99</VAR>\n");
        Assert.Equal(0, fired);
    }

    [Fact]
    public void DestTagsTheLineAndDoesNotLeakToTheNext()
    {
        (List<Line> lines, _) = Parse(Secure + "<DEST chat>Bob says hi</DEST>\nplain\n");
        Assert.Equal("chat", lines[0].Destination);
        Assert.Equal("Bob says hi", lines[0].PlainText);
        Assert.Null(lines[1].Destination);
    }

    [Fact]
    public void GaugeResolvesEntityValues()
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = true };
        var seen = new List<(string Name, double Value, double Max, string Caption)>();
        parser.MxpGauge += (n, v, m, c) => seen.Add((n, v, m, c));
        parser.Feed(Secure + "<!ENTITY hp 78><!ENTITY maxhp 120>"
                  + Secure + "<GAUGE hp max=maxhp caption=\"Health\">\n");
        (string name, double value, double max, string caption) = Assert.Single(seen);
        Assert.Equal("hp", name);
        Assert.Equal(78, value);
        Assert.Equal(120, max);
        Assert.Equal("Health", caption);
    }

    // ---- custom definitions --------------------------------------------------

    [Fact]
    public void CustomEntityExpandsAsText()
    {
        (List<Line> lines, _) = Parse(Secure + "<!ENTITY who \"Bjorn\">" + Secure + "hello &who;\n");
        Assert.Contains("hello Bjorn", lines[^1].PlainText);
    }

    /// <summary>An entity's value is text, never markup — otherwise a server could define an
    /// entity containing &lt;SEND&gt; and smuggle a clickable command onto an open line.</summary>
    [Fact]
    public void AnEntityCannotSmuggleMarkupOntoAnOpenLine()
    {
        (List<Line> lines, _) = Parse(
            Secure + "<!ENTITY evil \"<SEND href=quit>x</SEND>\">" + Open + "&evil;\n");
        Assert.Empty(lines[^1].Links);
        Assert.Contains("<SEND", lines[^1].PlainText);
    }

    [Fact]
    public void CustomElementSubstitutesNamedAttributes()
    {
        (List<Line> lines, _) = Parse(
            Secure + "<!ELEMENT bld '<SEND href=\"vbuild start &key;\" hint=\"&hint;\">' ATT='key hint'>"
          + Secure + "<bld key=warehouse hint=\"Upgrade\">Warehouse</bld>\n");
        Line line = lines[^1];
        Assert.Equal("Warehouse", line.PlainText);
        LinkInfo link = Assert.Single(line.Links).Link;
        Assert.Equal("vbuild start warehouse", link.Action);
        Assert.Equal("Upgrade", link.Hint);
    }

    [Fact]
    public void CustomElementSubstitutesPositionalAttributes()
    {
        (List<Line> lines, _) = Parse(
            Secure + "<!ELEMENT rm '<SEND href=\"look &t;\">' ATT='t'>"
          + Secure + "<rm sword>a sword</rm>\n");
        Assert.Equal("look sword", Assert.Single(lines[^1].Links).Link.Action);
    }

    [Fact]
    public void ADefinitionCannotRedefineABuiltInTag()
    {
        (List<Line> lines, _) = Parse(
            Secure + "<!ELEMENT b '<SEND href=quit>'>" + Secure + "<B>still bold</B>\n");
        Assert.Empty(lines[^1].Links);
        Assert.Contains(lines[^1].Runs, r => (r.Flags & RunFlags.Bold) != 0);
    }

    [Fact]
    public void DefinitionsAreRefusedOnAnOpenLine()
    {
        (List<Line> lines, _) = Parse("<!ENTITY x \"boom\">&x;\n");
        Assert.Contains("&x;", lines[^1].PlainText);
    }

    [Fact]
    public void AnUnknownTagIsStripped()
    {
        (List<Line> lines, _) = Parse(Secure + "<nosuchelement>text</nosuchelement>\n");
        Assert.Equal("text", lines[0].PlainText);
    }

    // ---- end to end through a session ----------------------------------------

    private static (MudSession Session, AnsiParser Parser) SessionWithMxp()
    {
        var session = new MudSession(new WorldProfile { Host = "localhost", Port = 1, EnableMxp = true });
        var parser = (AnsiParser)typeof(MudSession)
            .GetField("_ansi", System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Instance)!
            .GetValue(session)!;
        parser.MxpEnabled = true;
        return (session, parser);
    }

    [Fact]
    public void ServerVariablesAreNamespacedAwayFromTheUsersOwn()
    {
        (MudSession session, AnsiParser parser) = SessionWithMxp();
        parser.Feed(Secure + "<VAR roomname>Temple Square</VAR>\n");

        Assert.Equal("Temple Square", session.Variables.Get("mxp.roomname"));
        Assert.Null(session.Variables.Get("roomname"));   // a MUD cannot reach ${roomname}
        Assert.Equal("Temple Square", session.GameState.Get("mxp.var.roomname").Text);
    }

    [Fact]
    public void AMalformedVariableNameIsSanitised()
    {
        (MudSession session, AnsiParser parser) = SessionWithMxp();
        parser.Feed(Secure + "<VAR ../../evil>x</VAR>\n");

        Assert.Equal("x", session.Variables.Get("mxp.evil"));
        Assert.Null(session.Variables.Get("../../evil"));
    }

    [Fact]
    public void GaugePublishesToTheStateStore()
    {
        (MudSession session, AnsiParser parser) = SessionWithMxp();
        parser.Feed(Secure + "<!ENTITY hp 42><!ENTITY maxhp 90>"
                  + Secure + "<GAUGE hp max=maxhp caption=\"Health\">\n");

        Assert.Equal("42", session.GameState.Get("mxp.gauge.hp.value").Text);
        Assert.Equal("90", session.GameState.Get("mxp.gauge.hp.max").Text);
        Assert.Equal("Health", session.GameState.Get("mxp.gauge.hp.caption").Text);
    }

    [Fact]
    public void DestRoutesTheLineIntoACapturePane()
    {
        (MudSession session, AnsiParser parser) = SessionWithMxp();
        var routed = new List<(string Pane, string Text)>();
        session.LineRouted += (pane, line) => routed.Add((pane, line.PlainText));

        parser.Feed(Secure + "<DEST chat>Bjorn says hello</DEST>\n");

        Assert.Contains(routed, r => r.Pane == "chat" && r.Text == "Bjorn says hello");
    }
}
