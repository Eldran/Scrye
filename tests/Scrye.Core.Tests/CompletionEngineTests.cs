using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class CompletionEngineTests
{
    [Fact]
    public void CompletesByPrefixCaseInsensitive()
    {
        var e = new CompletionEngine(minLength: 3);
        e.Observe("You see a goblin guarding the gate.");
        var hits = e.Complete("ga");
        Assert.Contains("gate", hits);
        Assert.Contains("guarding", hits);
        Assert.DoesNotContain("goblin", hits);   // does not start with "ga"
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
