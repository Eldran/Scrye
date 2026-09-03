using Scrye.Core.Gmcp;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The reboot countdown fed by Mud.Status (3Scapes, 2 Sep 2026): one snapshot, then a delta
/// every ~122 s. The clock counts down between deltas on the session's own seconds and warns
/// once per threshold — the payload shapes here are verbatim from the 2 Sep capture.
/// </summary>
public class RebootClockTests
{
    [Theory]
    [InlineData(788790, "9d 3h")]
    [InlineData(86400, "1d")]
    [InlineData(3 * 3600 + 12 * 60 + 5, "3h 12m")]
    [InlineData(720, "12m")]
    [InlineData(45, "45s")]
    [InlineData(0, "0s")]
    public void Describe_uses_at_most_two_units(long seconds, string expected) =>
        Assert.Equal(expected, RebootClock.Describe(seconds));

    [Fact]
    public void Counts_down_between_deltas_and_resyncs_on_the_next()
    {
        var c = new RebootClock();
        Assert.False(c.Known);
        Assert.Equal("", c.StatusText);

        c.Observe("{\"full\":1,\"reboot_total\":882425,\"reboot_left\":790010,\"uptime\":92415,\"lag\":0.0}");
        Assert.True(c.Known);
        Assert.Equal(790010, c.SecondsLeft);
        Assert.Equal(882425, c.Total);

        for (int i = 0; i < 122; i++) c.Tick(1);
        Assert.Equal(790010 - 122, c.SecondsLeft);            // never two minutes stale

        c.Observe("{\"uptime\":92537,\"reboot_left\":789888}");
        Assert.Equal(789888, c.SecondsLeft);
        c.Observe("{\"uptime\":92600}");                       // no reboot_left: nothing moves
        Assert.Equal(789888, c.SecondsLeft);
        Assert.Null(c.TakeWarning());
    }

    [Fact]
    public void Warns_once_at_thirty_minutes_and_once_at_five()
    {
        var w = new RebootClock();
        w.Observe("{\"full\":1,\"reboot_left\":1801}");
        Assert.Null(w.TakeWarning());
        w.Tick(1);
        Assert.Equal("reboot in 30m", w.TakeWarning());
        Assert.Null(w.TakeWarning());                          // once

        for (int i = 0; i < 1500; i++) w.Tick(1);              // 300 left
        Assert.Equal("reboot in 5m", w.TakeWarning());
        Assert.Null(w.TakeWarning());

        for (int i = 0; i < 300; i++) w.Tick(1);
        Assert.Equal(0, w.SecondsLeft);                        // floors, never negative
        Assert.Equal("reboot in 0s", w.StatusText);
    }

    [Fact]
    public void A_countdown_that_jumps_up_is_the_next_cycle_and_rearms()
    {
        var w = new RebootClock();
        w.Observe("{\"full\":1,\"reboot_left\":1801}");
        w.Tick(1);
        Assert.Equal("reboot in 30m", w.TakeWarning());

        w.Observe("{\"uptime\":10,\"reboot_left\":882000}");    // the reboot happened
        Assert.Null(w.TakeWarning());
        w.Observe("{\"reboot_left\":1700}");
        Assert.Equal("reboot in 28m", w.TakeWarning());        // the 30m mark fires again
    }

    [Fact]
    public void Logging_in_already_under_a_threshold_shows_it_but_does_not_toast()
    {
        // Twenty minutes left at connect: the status row says so; a toast for a mark the
        // countdown was already past would be noise. The five-minute mark is still ahead.
        var l = new RebootClock();
        l.Observe("{\"full\":1,\"reboot_left\":1200}");
        Assert.Null(l.TakeWarning());
        Assert.Equal("reboot in 20m", l.StatusText);
        for (int i = 0; i < 900; i++) l.Tick(1);
        Assert.Equal("reboot in 5m", l.TakeWarning());
    }

    [Fact]
    public void A_stall_past_both_thresholds_warns_once()
    {
        var s = new RebootClock();
        s.Observe("{\"full\":1,\"reboot_left\":2000}");
        for (int i = 0; i < 1900; i++) s.Tick(1);
        Assert.Equal("reboot in 1m", s.TakeWarning());
        Assert.Null(s.TakeWarning());
    }

    [Fact]
    public void Reset_forgets_the_server()
    {
        var s = new RebootClock();
        s.Observe("{\"full\":1,\"reboot_left\":2000}");
        s.Reset();
        Assert.False(s.Known);
        Assert.Equal("", s.StatusText);
    }
}
