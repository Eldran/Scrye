using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// <c>.mxp</c>: what the server negotiated, which tags it sends, and which of them Scrye
/// strips.
///
/// <para>The stripped list is why this exists. Markup a client does not implement is thrown
/// away silently — correct behaviour, and it also makes "the server sends something we ignore"
/// and "the server sends nothing" identical from the output pane. Only one of those is a
/// feature waiting to be supported.</para>
/// </summary>
public class MxpAuditTests
{
    /// <summary>Drive real bytes through the real parser, with the audit wired the way the
    /// session wires it. Nothing here fakes a tag.</summary>
    private static MxpAudit Feed(string text, bool enabled = true)
    {
        var audit = new MxpAudit { Enabled = enabled, Negotiated = enabled };
        var ansi = new AnsiParser { MxpEnabled = enabled };
        ansi.MxpTagSeen += (name, secure, closing) => audit.Observe(name, secure, closing);
        ansi.MxpTagIgnored += audit.Ignored;
        ansi.Feed(text);
        return audit;
    }

    /// <summary>ESC[1z marks the rest of the line secure — the mode a server has to set before
    /// anything powerful is honoured.</summary>
    private const string Secure = "\x1b[1z";

    [Fact]
    public void Tags_the_server_sends_are_counted_by_name()
    {
        MxpAudit a = Feed($"{Secure}<SEND href='north'>north</SEND> and {Secure}<SEND>south</SEND>\r\n"
                          + "<B>bold</B>\r\n");

        // Two opens, not four: the matching </SEND> tags are not counted, or every paired
        // tag would read as twice as busy as it is.
        Assert.Equal(2, a.Snapshot().First(t => t.Name == "SEND").Count);
        Assert.Equal(1, a.Snapshot().First(t => t.Name == "B").Count);
    }

    [Fact]
    public void A_tag_Scrye_does_not_implement_is_reported_rather_than_vanishing()
    {
        // IMAGE is stripped on purpose. That is the right thing to do with it and the wrong
        // thing to be quiet about: it is a thing the server offers that you are not getting.
        MxpAudit a = Feed($"{Secure}<IMAGE url='map.png'>\r\n");

        MxpTagSeen image = a.Snapshot().Single(t => t.Name == "IMAGE");
        Assert.True(image.Ignored);
        Assert.Equal(1, a.IgnoredKinds);
        Assert.Contains("IGNORED", string.Join("\n", a.Report()));
    }

    [Fact]
    public void A_tag_Scrye_does_implement_is_not_reported_as_ignored()
    {
        MxpAudit a = Feed($"{Secure}<SEND href='north'>north</SEND>\r\n");
        Assert.False(a.Snapshot().Single(t => t.Name == "SEND").Ignored);
        Assert.Equal(0, a.IgnoredKinds);
    }

    [Fact]
    public void Whether_a_tag_arrived_secure_is_tallied_because_it_decides_whether_it_worked()
    {
        // A <SEND> on an open line is ignored by design. A server that never marks its lines
        // secure therefore produces a stream full of link tags and not one clickable link —
        // which looks like a broken client and is not.
        MxpAudit a = Feed("<SEND href='north'>north</SEND>\r\n"
                          + $"{Secure}<SEND href='south'>south</SEND>\r\n");

        MxpTagSeen send = a.Snapshot().Single(t => t.Name == "SEND");
        Assert.True(send.Open);
        Assert.True(send.Secure);
        Assert.Contains("both", string.Join("\n", a.Report()));
    }

    [Fact]
    public void Definitions_are_counted_under_the_kind_of_definition_they_are()
    {
        MxpAudit a = Feed($"{Secure}<!ELEMENT boldtext '<B>' FLAG=Bold>\r\n"
                          + $"{Secure}<!ENTITY hp '100'>\r\n");

        Assert.Contains(a.Snapshot(), t => t.Name == "!ELEMENT");
        Assert.Contains(a.Snapshot(), t => t.Name == "!ENTITY");
    }

    // ---- the three silences, told apart ------------------------------------

    [Fact]
    public void Mxp_switched_off_says_so_rather_than_blaming_the_server()
    {
        var a = new MxpAudit { Enabled = false };
        string report = string.Join("\n", a.Report());
        Assert.Contains("turned OFF", report);
        Assert.Contains("says anything about what the server can do", report);
    }

    [Fact]
    public void Never_negotiated_is_a_different_answer_from_negotiated_and_quiet()
    {
        var off = new MxpAudit { Enabled = true, Negotiated = false };
        Assert.Contains("not negotiated", string.Join("\n", off.Report()));

        var quiet = new MxpAudit { Enabled = true, Negotiated = true };
        string report = string.Join("\n", quiet.Report());
        Assert.Contains("negotiated: yes", report);
        Assert.Contains("no tag has arrived", report);
    }

    [Fact]
    public void Nothing_is_observed_while_the_parser_has_mxp_off()
    {
        // With the option refused the parser never enters a tag at all, so '<' is just text.
        MxpAudit a = Feed($"{Secure}<SEND href='north'>north</SEND>\r\n", enabled: false);
        Assert.Equal(0, a.TotalTags);
    }

    [Fact]
    public void A_reconnect_starts_the_tally_again()
    {
        MxpAudit a = Feed($"{Secure}<SEND href='north'>north</SEND>\r\n");
        Assert.NotEqual(0, a.TotalTags);

        a.Reset();
        Assert.Equal(0, a.TotalTags);
        Assert.Equal(0, a.TagsSeen);
        Assert.False(a.Negotiated);
    }

    [Fact]
    public void The_busiest_tag_is_listed_first()
    {
        var a = new MxpAudit { Negotiated = true };
        a.Observe("B", true);
        for (int i = 0; i < 5; i++) a.Observe("SEND", true);
        a.Observe("I", true);

        Assert.Equal("SEND", a.Snapshot()[0].Name);
    }
}
