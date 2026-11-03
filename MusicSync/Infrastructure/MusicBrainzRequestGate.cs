namespace MusicSync.Infrastructure;

/// <summary>
/// Paces MusicBrainz requests across every caller in the process (section 8.2: one request per second).
/// Register as a singleton so that transient typed HTTP clients share the same schedule.
/// </summary>
public sealed class MusicBrainzRequestGate
{
    private readonly SemaphoreSlim _turns = new(1, 1);
    private readonly TimeSpan _interval;
    private long _nextAllowedTicks = DateTime.MinValue.Ticks;

    public MusicBrainzRequestGate(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public async Task WaitForTurnAsync(CancellationToken ct)
    {
        await _turns.WaitAsync(ct);
        try
        {
            var wait = new DateTime(Interlocked.Read(ref _nextAllowedTicks), DateTimeKind.Utc) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);

            Interlocked.Exchange(ref _nextAllowedTicks, (DateTime.UtcNow + _interval).Ticks);
        }
        finally
        {
            _turns.Release();
        }
    }

    /// <summary>
    /// Pushes the next allowed request time out by <paramref name="delay"/>, for every caller.
    /// </summary>
    public void Penalize(TimeSpan delay)
    {
        var target = (DateTime.UtcNow + delay).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _nextAllowedTicks);
            if (target <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref _nextAllowedTicks, target, current) != current);
    }
}
