using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class CompletionEngineTests
{
    [Fact]
    public void CompletesByPrefixCaseInsensitive()
    {
        var e = new CompletionEngine(minLength: 3);
        // mixed casing + several "ga*" words so a lowercase prefix must match case-insensitively
        e.Observe("The Gate guard is Gathering by the gateway.");
        var hits = e.Complete("ga");
        Assert.Contains("Gate", hits);        // matches despite capital G
        Assert.Contains("Gathering", hits);
        Assert.Contains("gateway", hits);
        Assert.DoesNotContain("guard", hits);   // starts with "gu", not "ga"
    }

    [Fact]
    public void ExcludesShortWordsAndExactPrefix()
    {
        var e = new CompletionEngine(minLength: 3);
        e.Observe("go to the inn");   // "go", "to" are too short
        Assert.Empty(e.Complete("go"));
        e.Add("gossip");
        Assert.Equal(new[] { "gossip" }, e.Complete("gos"));
        Assert.Empty(e.Complete("gossip"));       // exact match is not its own completion
    }

    [Fact]
    public void MostRecentlySeenComesFirst()
    {
        var e = new CompletionEngine(minLength: 3);
        e.Add("gate");
        e.Add("gather");
        e.Add("gate");                            // re-seeing promotes it
        var hits = e.Complete("ga");
        Assert.Equal("gate", hits[0]);
    }

    [Fact]
    public void CapacityEvictsOldest()
    {
        var e = new CompletionEngine(minLength: 1, capacity: 2);
        e.Add("aaa"); e.Add("bbb"); e.Add("ccc");  // aaa evicted
        Assert.Equal(2, e.Count);
        Assert.Empty(e.Complete("aa"));
        Assert.Single(e.Complete("bb"));
    }

    [Fact]
    public void BlankPrefixReturnsNothing()
    {
        var e = new CompletionEngine();
        e.Observe("hello world");
        Assert.Empty(e.Complete(""));
    }
}
