using System;
using Scrye.Core.Net;
using Xunit;

namespace Scrye.Core.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void DelaysGrowExponentiallyAndCap()
    {
        var p = new ReconnectPolicy
        {
            BaseDelay = TimeSpan.FromSeconds(2),
            Factor = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60),
        };
        Assert.Equal(2, p.Delay(1).TotalSeconds);
        Assert.Equal(4, p.Delay(2).TotalSeconds);
        Assert.Equal(8, p.Delay(3).TotalSeconds);
        Assert.Equal(16, p.Delay(4).TotalSeconds);
        Assert.Equal(32, p.Delay(5).TotalSeconds);
        Assert.Equal(60, p.Delay(6).TotalSeconds);   // 64 capped to 60
        Assert.Equal(60, p.Delay(10).TotalSeconds);  // stays capped
    }

    [Fact]
    public void AttemptIsClampedToAtLeastOne()
    {
        var p = new ReconnectPolicy { BaseDelay = TimeSpan.FromSeconds(3) };
        Assert.Equal(3, p.Delay(0).TotalSeconds);
        Assert.Equal(3, p.Delay(-5).TotalSeconds);
    }

    [Fact]
    public void MaxAttemptsBoundsRetries()
    {
        var p = new ReconnectPolicy { MaxAttempts = 3 };
        Assert.True(p.ShouldRetry(0));
        Assert.True(p.ShouldRetry(2));
        Assert.False(p.ShouldRetry(3));
        Assert.False(p.ShouldRetry(4));
    }

    [Fact]
    public void ZeroMaxAttemptsMeansUnlimited()
    {
        var p = new ReconnectPolicy { MaxAttempts = 0 };
        Assert.True(p.ShouldRetry(1_000_000));
    }
}
