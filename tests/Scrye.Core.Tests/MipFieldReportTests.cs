using System.Collections.Generic;
using System.Linq;
using Scrye.Core.Automation;
using Scrye.Core.Mip;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// <c>.mip fields</c> — the report that answers "what does this character actually receive?".
/// It exists so a plugin can be written for a guild nobody here plays: the MUD keeps giving
/// guilds their own feeds, and before this the only trace of a tag Scrye did not decode was a
/// raw line scrolling past in the event log.
///
/// <para>What is pinned: a tag with no decoder is still counted, named, and parked somewhere a
/// plugin can read it; a field the server never sent is reported as absent rather than blank
/// (Vikings never send SP, and that is a fact worth seeing); and the markdown form survives
/// MIP's delimiters, which are the same character that ends a markdown table cell.</para>
/// </summary>
public class MipFieldReportTests
{
    private const string ElementalVitals =
        "A~3534~B~3534~C~933~D~759~E~1576~F~1576~G~100~H~100"
      + "~I~Emit : 16   Form: Time(1550)   Rating: 745"
      + "~J~Stones/Waves/Shrouds: 5/7/5(6%)   G2N: 30546574";

    private sealed class Rig
    {
        public readonly VariableStore Vars = new();
        public readonly MipShapeAudit Audit = new();
        public readonly Dictionary<string, string> Parked = new();   // stands in for state mip.<tag>
        public readonly MipProcessor Proc;

        public Rig()
        {
            Proc = new MipProcessor(Vars);
            Proc.VikingData += (k, v) => Audit.Observe(k, v);
            Proc.TagSeen += (tag, data, handled) =>
            {
                Audit.ObserveTag(tag, data, handled);
                if (!handled) Parked["mip." + tag.ToLowerInvariant()] = data;
            };
        }

        public void Send(string tag, string data) => Proc.Handle(new MipMessage("1", tag, data));

        public IReadOnlyList<(string, string?)> Vitals(params string[] names) =>
            names.Select(n => (n, Vars.Get(n))).ToList();

        public string Report(bool markdown = false) =>
            string.Join("\n", Audit.FieldReport(Vitals(
                "hp", "hpmax", "sp", "spmax", "gp1", "gp1max", "gp2", "gp2max",
                "gline1", "gline2", "enemy_name"), markdown));
    }

    [Fact]
    public void An_undecoded_tag_is_parked_where_a_plugin_can_read_it()
    {
        var rig = new Rig();

        rig.Send("EAA", "form:time|charge:1550|emit:16");
        rig.Send("EAA", "form:time|charge:1600|emit:17");

        Assert.Equal("form:time|charge:1600|emit:17", rig.Parked["mip.eaa"]);
    }

    [Fact]
    public void Decoded_tags_are_not_parked_because_they_already_have_homes()
    {
        var rig = new Rig();

        rig.Send("FFF", ElementalVitals);
        rig.Send("BBE", "RATING^^745");

        Assert.False(rig.Parked.ContainsKey("mip.fff"));
        Assert.False(rig.Parked.ContainsKey("mip.bbe"));
        Assert.Equal("933", rig.Vars.Get("sp"));       // and it still decoded normally
    }

    [Fact]
    public void The_report_names_an_undecoded_tag_and_where_to_read_it()
    {
        var rig = new Rig();
        rig.Send("FFF", ElementalVitals);
        rig.Send("EAA", "form:time|charge:1550");
        rig.Send("EAA", "form:time|charge:1600");

        string report = rig.Report();

        Assert.Contains("EAA", report);
        Assert.Contains("mip.eaa", report);
        Assert.Contains("x2", report);                 // two frames counted
        Assert.Contains("NOT DECODED", report);
    }

    [Fact]
    public void A_field_the_server_never_sent_is_reported_as_absent()
    {
        var rig = new Rig();
        rig.Send("FFF", ElementalVitals);

        string report = rig.Report();

        Assert.Contains("933", report);                // sp, which this guild does use
        Assert.Contains("(not sent)", report);         // enemy_name, which never arrived
    }

    [Fact]
    public void Feed_keys_are_listed_with_their_latest_value()
    {
        var rig = new Rig();
        rig.Send("BBE", "STONES^^5|7|5^^RATING^^745");
        rig.Send("BBE", "STONES^^6|7|5^^RATING^^812");

        string report = rig.Report();

        Assert.Contains("STONES", report);
        Assert.Contains("6|7|5", report);              // the latest, not the first
        Assert.Contains("812", report);
    }

    [Fact]
    public void Markdown_escapes_the_delimiters_that_would_break_a_table()
    {
        var rig = new Rig();
        rig.Send("BBE", "STONES^^6|7|5");

        IReadOnlyList<string> lines = rig.Audit.FieldReport(rig.Vitals("hp"), markdown: true);

        Assert.StartsWith("# MIP field report", lines[0]);
        // Every table row must have exactly the cells it declares: an unescaped pipe from the
        // payload — or from the shape fingerprint, which is MADE of delimiters — would add one.
        foreach (string row in lines.Where(l => l.StartsWith("| `STONES`")))
            Assert.Equal(5, row.Replace("\\|", "").Split('|').Length);
    }

    [Fact]
    public void An_empty_feed_says_why_rather_than_looking_broken()
    {
        var rig = new Rig();
        rig.Send("FFF", ElementalVitals);

        string report = rig.Report();

        Assert.Contains("none yet", report);
        Assert.Contains("vtoggle", report);            // the actual thing to go and do
    }
}
