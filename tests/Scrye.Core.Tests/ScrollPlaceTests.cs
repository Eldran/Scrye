using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// A scrollback view's place, kept while the view shows another world (Joakim, 30 Oct 2026:
/// switching between connected characters threw the chat pane back to the start of the
/// session). Lines are 10 px here, the viewport 50 px - five lines.
/// </summary>
public sealed class ScrollPlaceTests
{
    [Fact]
    public void AFollowingViewComesBackToTheNewestLine()
    {
        // 100 lines, at the bottom (offset 950 = lines 95-99 showing)
        ScrollPlace p = ScrollPlace.Capture(following: true, offsetY: 950, lineHeight: 10, baseSequence: 0, count: 100, pending: 0);
        // 30 more arrived while away: the bottom is now 1250, not 950
        var (y, follow, pending) = p.Restore(baseSequence: 0, count: 130, lineHeight: 10, viewport: 50);
        Assert.Equal(1250.0, y);
        Assert.True(follow);
        Assert.Equal(0, pending);
    }

    [Fact]
    public void AViewScrolledBackComesBackToTheSameLineAndCountsWhatArrived()
    {
        // reading line 20 at the top, 3 lines already waiting
        ScrollPlace p = ScrollPlace.Capture(false, 200, 10, 0, 100, 3);
        var (y, follow, pending) = p.Restore(0, 112, 10, 50);
        Assert.Equal(200.0, y);
        Assert.False(follow);
        Assert.Equal(15, pending);                                     // 3 waiting + 12 arrived
    }

    [Fact]
    public void ThePlaceIsALineNotAPixelSoATrimDoesNotMoveIt()
    {
        // line 520 of the session at the top; then 500 lines are trimmed off the front
        ScrollPlace p = ScrollPlace.Capture(false, 4200, 10, baseSequence: 100, count: 1000, pending: 0);
        Assert.Equal(520.0, p.TopLine);
        var (y, follow, _) = p.Restore(baseSequence: 600, count: 600, lineHeight: 10, viewport: 50);
        Assert.Equal(0.0, y);                                          // line 520 is gone: the oldest line kept
        Assert.False(follow);
        // and a line that is still there is found at its new index
        var (y2, _, _) = p.Restore(baseSequence: 500, count: 700, lineHeight: 10, viewport: 50);
        Assert.Equal(200.0, y2);                                       // line 520 is index 20 now
    }

    [Fact]
    public void AFractionalOffsetIsKeptExactly()
    {
        ScrollPlace p = ScrollPlace.Capture(false, 123.5, 10, 0, 100, 0);
        Assert.Equal(123.5, p.Restore(0, 100, 10, 50).OffsetY, 6);
    }

    [Fact]
    public void APlaceThatLandsAtTheBottomIsFollowingAgain()
    {
        // reading near the end; the buffer shrank (a clear and refill) so the place is past the bottom
        ScrollPlace p = ScrollPlace.Capture(false, 900, 10, 0, 100, 2);
        var (y, follow, pending) = p.Restore(0, 60, 10, 50);
        Assert.Equal(550.0, y);
        Assert.True(follow);
        Assert.Equal(0, pending);
        // ...within the view's 2 px tolerance as well
        var near = ScrollPlace.Capture(false, 549, 10, 0, 60, 0).Restore(0, 60, 10, 50);
        Assert.True(near.Following);
        var edge = ScrollPlace.Capture(false, 548, 10, 0, 60, 0).Restore(0, 60, 10, 50);
        Assert.True(edge.Following);                                   // exactly 2 px short counts
        var above = ScrollPlace.Capture(false, 547, 10, 0, 60, 0).Restore(0, 60, 10, 50);
        Assert.False(above.Following);
    }

    [Fact]
    public void ABufferThatShrankAddsNoNegativeNewLines()
    {
        // 100 lines when the place was taken, 3 waiting; now only 80 (cleared and refilled)
        var r = ScrollPlace.Capture(false, 200, 10, 0, 100, 3).Restore(0, 80, 10, 50);
        Assert.False(r.Following);
        Assert.Equal(3, r.Pending);
    }

    [Fact]
    public void AViewportNotLaidOutYetCountsAsNoHeight()
    {
        var r = ScrollPlace.Capture(true, 0, 10, 0, 100, 0).Restore(0, 100, 10, viewport: -50);
        Assert.Equal(1000.0, r.OffsetY);
    }

    [Fact]
    public void AShortBufferHasItsBottomAtTheTop()
    {
        // three lines in a five-line viewport: nothing to scroll, so following at 0
        var r = ScrollPlace.Capture(false, 0, 10, 0, 3, 0).Restore(0, 3, 10, 50);
        Assert.Equal(0.0, r.OffsetY);
        Assert.True(r.Following);
    }

    [Fact]
    public void CaptureNeverRecordsNonsense()
    {
        ScrollPlace following = ScrollPlace.Capture(true, 300, 10, 0, 100, 7);
        Assert.Equal(0, following.PendingAtSave);                      // following has nothing waiting
        ScrollPlace neg = ScrollPlace.Capture(false, -40, 10, 5, 100, -3);
        Assert.Equal(5.0, neg.TopLine);                                // a negative offset is the top
        Assert.Equal(0, neg.PendingAtSave);
        Assert.Equal(105L, neg.TotalAtSave);
        // no line height yet (never laid out): the top, and restored as following
        ScrollPlace none = ScrollPlace.Capture(false, 300, 0, 0, 100, 0);
        Assert.Equal(0.0, none.TopLine);
        Assert.True(none.Restore(0, 100, 0, 50).Following);
    }
}
