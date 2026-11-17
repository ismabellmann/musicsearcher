using System.Collections.Concurrent;

namespace MusicSync.Worker;

/// <summary>Owns cancellation sources for jobs currently being processed by the worker.</summary>
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _jobs = new();

    public CancellationToken Begin(int jobId, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_jobs.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Job {jobId} is already being processed.");
        }
        return source.Token;
    }

    public bool Cancel(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var source))
            return false;
        source.Cancel();
        return true;
    }

    public void End(int jobId)
    {
        if (_jobs.TryRemove(jobId, out var source))
            source.Dispose();
    }
}
