namespace Scrye.Core.Net;

/// <summary>
/// Exponential-backoff schedule for automatic reconnection. Pure and
/// deterministic (no clock, no randomness) so it is trivially testable:
/// attempt <c>n</c> waits <c>BaseDelay · Factor^(n-1)</c>, capped at
/// <see cref="MaxDelay"/>. <see cref="MaxAttempts"/> bounds the retries
/// (0 = retry forever). The session owns one of these and asks it for the next
/// delay each time a live connection drops.
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>Delay before the first retry.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Upper bound on any single delay.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Growth factor between successive attempts.</summary>
    public double Factor { get; set; } = 2.0;
    /// <summary>Maximum retries before giving up; 0 means retry indefinitely.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Whether the session should keep retrying after <paramref name="attempt"/> tries so far.</summary>
    public bool ShouldRetry(int attempt) => MaxAttempts <= 0 || attempt < MaxAttempts;

    /// <summary>The wait before the <paramref name="attempt"/>-th retry (1-based).</summary>
    public TimeSpan Delay(int attempt)
    {
        if (attempt < 1) attempt = 1;
        double secs = BaseDelay.TotalSeconds * Math.Pow(Factor <= 0 ? 1 : Factor, attempt - 1);
        double cap = MaxDelay.TotalSeconds;
        if (double.IsNaN(secs) || double.IsInfinity(secs) || secs > cap) secs = cap;
        if (secs < 0) secs = 0;
        return TimeSpan.FromSeconds(secs);
    }
}
