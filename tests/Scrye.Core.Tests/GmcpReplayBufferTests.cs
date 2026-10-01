using System.Linq;
using Scrye.Core.Gmcp;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The GMCP a plugin loaded mid-session is caught up with (Joakim, 1 Oct 2026: "sometimes if
/// you enable a plugin while Scrye is running it wont populate with data, but when you restart
/// it populates"). Most of the 3Scapes feed is a full report at login and changes after it, so
/// what is kept must rebuild the present - and nothing that would happen twice.
/// </summary>
public class GmcpReplayBufferTests
{
    private static string[] Of(GmcpReplayBuffer b, string pkg) =>
        b.Snapshot().Where(m => m.Package == pkg).Select(m => m.Json).ToArray();

    [Fact]
    public void AWholePackageKeepsOnlyItsLastMessage()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Char.Vitals", """{ "hp": 10 }""");
        b.Record("Char.Vitals", """{ "hp": 12 }""");
        Assert.Equal(new[] { """{ "hp": 12 }""" }, Of(b, "Char.Vitals"));
    }

    [Fact]
    public void ASnapshotKeepsItsChangesAndANewSnapshotStartsOver()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Merc.Vitals", """{ "full": 1, "hp": 50, "hp_max": 80, "stam": 9 }""");
        b.Record("Merc.Vitals", """{ "stam": 8 }""");
        b.Record("Merc.Vitals", """{ "stam": 7 }""");
        Assert.Equal(3, Of(b, "Merc.Vitals").Length);
        b.Record("Merc.Vitals", """{ "full": 1, "hp": 80, "hp_max": 80, "stam": 9 }""");
        Assert.Equal(new[] { """{ "full": 1, "hp": 80, "hp_max": 80, "stam": 9 }""" }, Of(b, "Merc.Vitals"));
    }

    [Fact]
    public void APagedReportIsKeptWholeAndAFullBurstReplacesTheOldOne()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Guild.Kingdom", """{ "page": 1, "pages": 2, "full": 1, "grudges": [ { "town": "Ribe" } ] }""");
        b.Record("Guild.Kingdom", """{ "page": 2, "pages": 2, "standings": [ 1 ] }""");
        b.Record("Guild.Kingdom", """{ "vrep": 3 }""");                               // an unpaged change
        Assert.Equal(3, Of(b, "Guild.Kingdom").Length);

        b.Record("Guild.Kingdom", """{ "page": 1, "pages": 2, "full": 1, "grudges": [] }""");
        Assert.Equal(4, Of(b, "Guild.Kingdom").Length);                               // still arriving: both kept
        b.Record("Guild.Kingdom", """{ "page": 2, "pages": 2, "standings": [ 2 ] }""");
        Assert.Equal(2, Of(b, "Guild.Kingdom").Length);                               // the new report alone
        Assert.Contains("\"grudges\": []", Of(b, "Guild.Kingdom")[0]);
    }

    [Fact]
    public void EventsAreNeverReplayed()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Comm.Channel.Text", """{ "channel": "ooc", "text": "hi" }""");
        b.Record("Room.Death", """{ "name": "guard" }""");
        b.Record("Core.Supported", """{ "Guild.City": 1 }""");
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void ReplayKeepsArrivalOrderAcrossPackagesAndAReconnectClearsIt()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Room.Info", """{ "num": 1 }""");
        b.Record("Guild.City", """{ "nexttick": 5 }""");
        b.Record("Room.Info", """{ "num": 2 }""");
        Assert.Equal(new[] { "Guild.City", "Room.Info" }, b.Snapshot().Select(m => m.Package).ToArray());
        b.Clear();
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void ALongRunOfChangesIsFoldedIntoTheSnapshotNotDropped()
    {
        var b = new GmcpReplayBuffer();
        b.Record("Merc.Vitals", """{ "full": 1, "hp": 50, "hp_max": 80, "pos": { "x": 1, "y": 1 } }""");
        for (int i = 0; i < GmcpReplayBuffer.MaxMessagesPerPackage + 40; i++)
            b.Record("Merc.Vitals", "{ \"stam\": " + i + ", \"pos\": { \"x\": " + i + " } }");
        string[] kept = Of(b, "Merc.Vitals");
        Assert.True(kept.Length <= GmcpReplayBuffer.MaxMessagesPerPackage);
        Assert.Contains("\"hp_max\":80", kept[0]);          // the snapshot survived, folded forward
        Assert.Contains("\"y\":1", kept[0]);                // objects merge key by key
    }

    [Fact]
    public void MergeReplacesValuesAndListsAndMergesObjects()
    {
        Assert.Equal("""{"a":1,"b":{"c":2,"d":4},"l":[9]}""",
            GmcpReplayBuffer.Merge("""{"a":1,"b":{"c":1,"d":4},"l":[1,2]}""", """{"b":{"c":2},"l":[9]}"""));
        Assert.Equal("not json", GmcpReplayBuffer.Merge("{}", "not json"));
    }
}
