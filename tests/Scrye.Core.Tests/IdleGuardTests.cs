using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The idle guard (<see cref="IdleGuard"/>). The behaviour worth pinning is the once-per-stretch
/// rule — firing suspends automation and tells every plugin, so doing it sixty times a minute
/// would be both loud and wrong — and that time only accrues while the guard is actually on.
/// </summary>
public class IdleGuardTests
{
    private static (int Warnings, int Fires) Run(IdleGuard g, int seconds)
    {
        int w = 0, f = 0;
        for (int i = 0; i < seconds; i++)
            switch (g.Tick(1.0))
            {
                case IdleGuardSignal.Warning: w++; break;
                case IdleGuardSignal.Fired: f++; break;
            }
        return (w, f);
    }

    private static IdleGuard On(int seconds) => new() { Seconds = seconds, Enabled = true };

    [Fact]
    public void Warns_at_eighty_percent_then_fires_at_the_limit()
    {
        IdleGuard g = On(100);

        Assert.Equal((0, 0), Run(g, 79));
        Assert.Equal((1, 0), Run(g, 1));      // crosses 80
        Assert.Equal((0, 0), Run(g, 19));     // the warning does not repeat
        Assert.Equal((0, 1), Run(g, 1));      // crosses 100
        Assert.True(g.HasFired);
    }

    [Fact]
    public void Fires_once_per_idle_stretch_however_long_it_runs()
    {
        IdleGuard g = On(60);
        Run(g, 60);
        Assert.Equal((0, 0), Run(g, 3600));   // an hour further on, still silent
    }

    [Fact]
    public void A_poke_re_arms_both_the_warning_and_the_firing()
    {
        IdleGuard g = On(100);
        Run(g, 100);
        Assert.True(g.HasFired);

        g.Poke();
        Assert.False(g.HasFired);
        Assert.Equal(0, g.IdleSeconds);
        Assert.Equal((1, 1), Run(g, 100));    // the next stretch is judged on its own
    }

    [Fact]
    public void While_disabled_no_time_accrues_at_all()
    {
        var g = new IdleGuard { Seconds = 60 };
        Assert.Equal((0, 0), Run(g, 5000));
        Assert.Equal(0, g.IdleSeconds);
    }

    [Fact]
    public void Enabling_it_starts_a_fresh_stretch_rather_than_firing_on_history()
    {
        var g = new IdleGuard { Seconds = 60 };
        Run(g, 5000);
        g.Enabled = true;
        Assert.Equal((0, 0), Run(g, 47));     // still short of the 48s warning
        Assert.Equal((1, 0), Run(g, 1));
    }

    [Fact]
    public void Changing_the_limit_re_arms_it()
    {
        IdleGuard g = On(100);
        Run(g, 90);                            // warned already
        g.Seconds = 300;
        Assert.Equal(0, g.IdleSeconds);
        Assert.Equal(300, g.SecondsRemaining);
    }

    [Fact]
    public void A_single_huge_step_fires_rather_than_only_warning()
    {
        // a laptop resuming from sleep, or a debugger pause: the outcome that matters is that it
        // fired, not an announcement about a threshold it blew past several minutes ago
        Assert.Equal(IdleGuardSignal.Fired, On(600).Tick(9999));
    }

    [Theory]
    [InlineData(1, IdleGuard.MinSeconds)]
    [InlineData(0, IdleGuard.MinSeconds)]
    [InlineData(-5, IdleGuard.MinSeconds)]
    [InlineData(999999, IdleGuard.MaxSeconds)]
    [InlineData(600, 600)]
    public void The_limit_is_clamped_to_something_sane(int set, int expected)
    {
        var g = new IdleGuard { Seconds = set };
        Assert.Equal(expected, g.Seconds);
    }

    [Fact]
    public void It_is_off_by_default()
    {
        var g = new IdleGuard();
        Assert.False(g.Enabled);
        Assert.Equal(IdleGuard.DefaultSeconds, g.Seconds);
    }

    [Fact]
    public void Remaining_counts_down_and_bottoms_out_at_zero()
    {
        IdleGuard g = On(100);
        Run(g, 40);
        Assert.Equal(60, g.SecondsRemaining);
        Run(g, 100);
        Assert.Equal(0, g.SecondsRemaining);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m30s")]
    [InlineData(600, "10m")]
    [InlineData(630, "10m30s")]
    public void Durations_read_the_way_a_person_would_say_them(double seconds, string expected) =>
        Assert.Equal(expected, IdleGuard.Describe(seconds));
}
