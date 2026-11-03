namespace MusicSync.Core;

/// <summary>
/// Automatic retry budget and exponential backoff for transient job failures (section 8.5).
/// </summary>
public sealed class RetryPolicy
{
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMinutes(10);

    public RetryPolicy(int maxAutomaticRetries, TimeSpan baseDelay, TimeSpan? maxDelay = null)
    {
        if (maxAutomaticRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAutomaticRetries));
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay));

        MaxAutomaticRetries = maxAutomaticRetries;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay ?? DefaultMaxDelay;
    }

    public int MaxAutomaticRetries { get; }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Delay before automatic retry number <paramref name="retryNumber"/> (1-based): base, 2x base, 4x base, capped.
    /// </summary>
    public TimeSpan DelayForRetry(int retryNumber)
    {
        if (retryNumber < 1)
            return TimeSpan.Zero;

        var factor = Math.Pow(2, retryNumber - 1);
        var delay = TimeSpan.FromTicks((long)Math.Min(BaseDelay.Ticks * factor, MaxDelay.Ticks));
        return delay;
    }
}
