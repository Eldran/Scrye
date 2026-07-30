using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void AddSkipsEmptyAndConsecutiveDuplicates()
    {
        var h = new CommandHistory();
        h.Add("look");
        h.Add("");            // ignored
        h.Add("north");
        h.Add("north");       // consecutive dup ignored
        h.Add("look");        // not consecutive -> kept
        Assert.Equal(new[] { "look", "north", "look" }, h.Items);
    }

    [Fact]
    public void PreviousWalksBackAndClampsAtOldest()
    {
        var h = new CommandHistory();
        h.Add("a"); h.Add("b");
        Assert.Equal("b", h.Previous(""));
        Assert.Equal("a", h.Previous(""));
        Assert.Equal("a", h.Previous(""));   // clamped
    }

    [Fact]
    public void NextRestoresDraftThenReturnsNull()
    {
        var h = new CommandHistory();
        h.Add("a"); h.Add("b");
        Assert.Equal("b", h.Previous("draft"));   // begin nav, save draft
        Assert.Equal("a", h.Previous("draft"));
        Assert.Equal("b", h.Next());
        Assert.Equal("draft", h.Next());          // back to the live draft
        Assert.Null(h.Next());                    // nothing past the end
    }

    [Fact]
    public void PreviousOnEmptyHistoryIsNull()
    {
        var h = new CommandHistory();
        Assert.Null(h.Previous("x"));
        Assert.Null(h.Next());
    }

    [Fact]
    public void AddResetsNavigationToEnd()
    {
        var h = new CommandHistory();
        h.Add("a"); h.Add("b");
        h.Previous("");                 // navigate up
        h.Add("c");                     // submitting resets the cursor
        Assert.Equal("c", h.Previous("")); // up now starts from the newest
    }

    [Fact]
    public void RespectsCapacity()
    {
        var h = new CommandHistory(capacity: 3);
        h.Add("1"); h.Add("2"); h.Add("3"); h.Add("4");
        Assert.Equal(new[] { "2", "3", "4" }, h.Items);   // oldest dropped
    }
}
