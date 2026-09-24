using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scrye.Core.Gmcp;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The GMCP shape memory: what the feed looked like in earlier sessions, so a package or field
/// the server adds is named the day it appears instead of being found by reading two field
/// reports side by side (Joakim, 24 Sep 2026: "is there a way to see if new gmcp fields get
/// added?"). Payloads are the 3Scapes ones from the September captures.
/// </summary>
public class GmcpShapeTests
{
    private const string City = """{ "blot": { "total": 9, "state": "rest" }, "nexttick": 5140, "guild": "viking" }""";
    private const string CityWithProduction =
        """{ "blot": { "total": 9, "state": "rest" }, "nexttick": 5140, "production": { "grain": 12 }, "dcycle": { "secs": 1, "name": "Calm Season" }, "guild": "viking" }""";

    /// <summary>A memory that has seen <paramref name="payloads"/> in an earlier session.</summary>
    private static (GmcpAudit audit, List<string> said) Remembering(params (string pkg, string json)[] payloads)
    {
        var first = new GmcpAudit();
        foreach ((string p, string j) in payloads) first.Observe(p, j);
        var audit = new GmcpAudit();
        audit.Shape.LoadJson(first.Shape.ToJson());
        var said = new List<string>();
        audit.Shape.Announce = said.Add;
        return (audit, said);
    }

    [Fact]
    public void TheFirstSessionIsTheBaselineAndSaysNothing()
    {
        var audit = new GmcpAudit();
        var said = new List<string>();
        audit.Shape.Announce = said.Add;
        Assert.True(audit.Shape.Baseline);
        audit.Observe("Guild.City", City);
        audit.Observe("Room.Death", """{ "corpse": 1, "killer": "Goran", "npc": 1, "name": "Wiremouth guard" }""");
        Assert.Empty(said);
        Assert.Contains(audit.ChangesReport(), l => l.Contains("this one is the baseline"));
    }

    [Fact]
    public void ANewFieldIsAnnouncedOnceAndListedInTheReport()
    {
        (GmcpAudit audit, List<string> said) = Remembering(("Guild.City", City));
        audit.Observe("Guild.City", City);
        Assert.Empty(said);                                     // nothing new: nothing said
        audit.Observe("Guild.City", CityWithProduction);
        Assert.Equal(new[] { "GMCP: new fields in Guild.City: production.grain, dcycle.secs, dcycle.name" }, said);
        audit.Observe("Guild.City", CityWithProduction.Replace("12", "13"));
        Assert.Single(said);                                    // once, not per message
        string changes = string.Join("\n", audit.ChangesReport());
        Assert.Contains("**new fields** in `Guild.City`: `production.grain`, `dcycle.secs`, `dcycle.name`", changes);
    }

    [Fact]
    public void ANewPackageIsAnnouncedWithoutItsFieldsBesideIt()
    {
        (GmcpAudit audit, List<string> said) = Remembering(("Guild.City", City));
        audit.Observe("Char.XP", """{ "xp": 198923091820, "per_hour": 56046676, "gain30": 28023338 }""");
        Assert.Equal(new[] { "GMCP: new package Char.XP - never sent on this MUD before ('.gmcp Char.XP' shows it)" }, said);
        string changes = string.Join("\n", audit.ChangesReport());
        Assert.Contains("**new package** `Char.XP`", changes);
        Assert.DoesNotContain("in `Char.XP`", changes);         // its fields are not news twice over
    }

    [Fact]
    public void IndicesSlicesAndKeyedMapsFoldToOneField()
    {
        Assert.Equal("Room.Contents|items[].name", GmcpShapeMemory.FieldKey("Room.Contents", "items[3].name"));
        Assert.Equal("Guild.Roster|hird_#[].level", GmcpShapeMemory.FieldKey("Guild.Roster", "hird_1[0].level"));
        Assert.Equal("Guild.Market|market_#[].price", GmcpShapeMemory.FieldKey("Guild.Market", "market_0[0].price"));
        Assert.Equal("Room.Info|exits.*", GmcpShapeMemory.FieldKey("Room.Info", "exits.jump"));
        Assert.Equal("Room.Map|legend.*", GmcpShapeMemory.FieldKey("Room.Map", "legend.@"));
        Assert.Equal("Guild.State|gxp.buandi_last", GmcpShapeMemory.FieldKey("Guild.State", "gxp.buandi_last"));

        // walking into a room with an exit name never seen before is not a new field
        (GmcpAudit audit, List<string> said) = Remembering(
            ("Room.Info", """{ "exits": { "n": 5 }, "area": "Unknown", "name": "Road", "num": 4 }"""));
        audit.Observe("Room.Info", """{ "exits": { "jump": 5, "portal": 104 }, "area": "Unknown", "name": "Gate", "num": 12 }""");
        Assert.Empty(said);
    }

    [Fact]
    public void AnEmptyListIsTheSameFieldAsAFullOne()
    {
        // Guild.Trade's carts are a list of records when carts are out and [] when none are:
        // an empty list is not a new field because yesterday's was full
        (GmcpAudit audit, List<string> said) = Remembering(
            ("Guild.Trade", """{ "carts": [ { "good": "mead", "secs": 4195 } ] }"""));
        audit.Observe("Guild.Trade", """{ "carts": [ ] }""");
        Assert.Empty(said);

        // ...but the first time a list that was always empty has something in it, what its
        // records look like IS news
        (GmcpAudit audit2, List<string> said2) = Remembering(("Guild.City", """{ "builds": [ ] }"""));
        audit2.Observe("Guild.City", """{ "builds": [ { "id": "smithy", "secs": 60 } ] }""");
        Assert.Equal(new[] { "GMCP: new fields in Guild.City: builds[].id, builds[].secs" }, said2);
    }

    [Fact]
    public void FieldsAndPackagesThatDidNotTurnUpAreListedAsAHint()
    {
        (GmcpAudit audit, _) = Remembering(("Guild.City", CityWithProduction), ("Guild.Fleet", """{ "ships": [] }"""));
        audit.Observe("Guild.City", City);
        string changes = string.Join("\n", audit.ChangesReport());
        Assert.Contains("- `Guild.City`: `dcycle.name`, `dcycle.secs`, `production.grain`", changes);
        Assert.Contains("Packages sent before but not in this session: `Guild.Fleet`", changes);
    }

    [Fact]
    public void AnOfferedPackageOutsideTheSubscriptionIsSaidTheMomentCoreSupportedLands()
    {
        (GmcpAudit audit, List<string> said) = Remembering(("Guild.City", City));
        audit.SubscriptionSent = """["Char 1","Char.XP 1","Room 1","Guild 1"]""";
        audit.SubscriptionVerb = "Core.Supports.Set";
        // the pre-subscription answer (all 0) says nothing
        audit.Observe("Core.Supported", """{ "Char.Vitals": 0, "Quest.Log": 0 }""");
        Assert.Empty(said);
        audit.Observe("Core.Supported", """{ "Char.Vitals": 1, "Char.XP": 1, "Room.Death": 1, "Guild.City": 1, "Quest.Log": 1 }""");
        Assert.Equal(new[] { "GMCP: the server offers Quest.Log but Scrye does not subscribe to it - nothing from it can arrive ('.gmcp new')" }, said);
        audit.Observe("Core.Supported", """{ "Char.Vitals": 1, "Char.XP": 1, "Room.Death": 1, "Guild.City": 1, "Quest.Log": 1, "Mud.Status": 1 }""");
        Assert.Single(said);                                    // once per connection

        audit.Observe("Char.Vitals", """{ "hp": 1 }""");
        Assert.True(audit.Covers("Char.XP"));
        Assert.True(audit.Covers("Guild.City"));
        Assert.False(audit.Covers("Quest.Log"));
        Assert.Equal(new[] { "Char.XP", "Room.Death", "Guild.City" }, audit.OfferedNotArrived());   // Mud.Status: not subscribed
        string changes = string.Join("\n", audit.ChangesReport());
        Assert.Contains("**Offered but not subscribed**", changes);
        Assert.Contains("`Quest.Log`", changes);
        Assert.Contains("Offered and subscribed, but nothing arrived this session: `Char.XP`, `Room.Death`, `Guild.City`", changes);
    }

    [Fact]
    public void WatchOffLearnsWithoutSaying()
    {
        (GmcpAudit audit, List<string> said) = Remembering(("Guild.City", City));
        audit.Shape.Watch = false;
        audit.Observe("Guild.City", CityWithProduction);
        Assert.Empty(said);
        Assert.Contains("Guild.City|production.grain", audit.Shape.NewThisSession);
    }

    [Fact]
    public void AReconnectKeepsWhatWasLearnedAndTheFileCarriesItToTheNextSession()
    {
        string file = Path.Combine(Path.GetTempPath(), "scrye-shape-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var a = new GmcpAudit();
            a.UseShapeFile(file);
            a.Observe("Guild.City", City);                      // baseline session
            a.Reset();                                          // reconnect: saved, and now known
            Assert.True(File.Exists(file));
            var said = new List<string>();
            a.Shape.Announce = said.Add;
            a.Observe("Guild.City", CityWithProduction);
            Assert.Single(said);
            a.Reset();
            a.Observe("Guild.City", CityWithProduction);
            Assert.Single(said);                                // learned last connection: not news now

            var b = new GmcpAudit();                            // a new start of the client
            b.UseShapeFile(file);
            Assert.False(b.Shape.Baseline);
            Assert.True(b.Shape.KnownCount >= 5);
            var said2 = new List<string>();
            b.Shape.Announce = said2.Add;
            b.Observe("Guild.City", CityWithProduction);
            Assert.Empty(said2);

            File.WriteAllText(file, "not json");                // a damaged memory starts over
            var c = new GmcpAudit();
            c.UseShapeFile(file);
            Assert.True(c.Shape.Baseline);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public void TheFieldReportOpensWithTheChanges()
    {
        (GmcpAudit audit, _) = Remembering(("Guild.City", City));
        audit.Observe("Guild.City", CityWithProduction);
        IReadOnlyList<string> report = audit.FieldReport("Goran");
        int changes = report.ToList().FindIndex(l => l == "## Changes since earlier sessions");
        int city = report.ToList().FindIndex(l => l == "## Guild.City");
        Assert.True(changes > 0 && changes < city);
    }
}
