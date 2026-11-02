using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Infrastructure;

namespace MusicSync.Worker;

/// <summary>
/// Processes persisted download jobs and stops after selecting a candidate,
/// leaving the job ready for the later tagging pipeline.
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;
    private readonly IAcquisitionProvider _acquisitionProvider;
    private readonly IMatchScorer _matchScorer;

    public Worker(
        IServiceScopeFactory scopeFactory,
        ILogger<Worker> logger,
        IAcquisitionProvider acquisitionProvider,
        IMatchScorer matchScorer)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _acquisitionProvider = acquisitionProvider;
        _matchScorer = matchScorer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download worker started.");
        await RecoverInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextJobAsync(stoppingToken))
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download worker failed while polling or processing a job.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("Download worker stopped.");
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        await db.DownloadJobs
            .Where(job => job.Status == DownloadJobStatus.Resolving || job.Status == DownloadJobStatus.Searching)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, DownloadJobStatus.Queued)
                    .SetProperty(job => job.FinishedAt, (DateTime?)null),
                ct);
    }

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var jobId = await db.DownloadJobs
            .Where(item => item.Status == DownloadJobStatus.Queued)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => (int?)item.Id)
            .FirstOrDefaultAsync(ct);

        if (jobId is null)
            return false;

        var startedAt = DateTime.UtcNow;
        var claimed = await db.DownloadJobs
            .Where(item => item.Id == jobId.Value && item.Status == DownloadJobStatus.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, DownloadJobStatus.Resolving)
                    .SetProperty(item => item.StartedAt, item => item.StartedAt ?? startedAt)
                    .SetProperty(item => item.Attempts, item => item.Attempts + 1)
                    .SetProperty(item => item.Error, (string?)null),
                ct);

        if (claimed == 0)
            return true;

        var job = await db.DownloadJobs.SingleAsync(item => item.Id == jobId.Value, ct);

        try
        {
            if (!string.IsNullOrEmpty(job.ProviderName)
                && !string.Equals(job.ProviderName, _acquisitionProvider.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Acquisition provider '{job.ProviderName}' is not registered.");
            }

            var track = await db.Tracks.FindAsync(new object[] { job.TrackId }, ct);
            if (track is null)
            {
                Fail(job, $"Track {job.TrackId} was not found");
                await db.SaveChangesAsync(ct);
                return true;
            }

            var trackInfo = new TrackInfo
            {
                RecordingMbid = track.RecordingMbid,
                Title = track.Title,
                ArtistName = track.ArtistName,
                AlbumTitle = track.AlbumTitle,
                Isrc = track.Isrc,
                DurationMs = track.DurationMs
            };

            job.Status = DownloadJobStatus.Searching;
            await db.SaveChangesAsync(ct);

            var candidates = await _acquisitionProvider.FindCandidatesAsync(trackInfo, 5, ct);
            if (candidates.Count == 0)
            {
                Fail(job, "No matching candidates found");
                await db.SaveChangesAsync(ct);
                return true;
            }

            var best = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Result = _matchScorer.Score(trackInfo, candidate)
                })
                .OrderByDescending(item => item.Result.Score)
                .First();

            job.SourceUrl = best.Candidate.SourceUrl;
            job.MatchScore = (float)best.Result.Score;

            if (best.Result.AutoAccept)
            {
                job.Status = DownloadJobStatus.ReadyForTagging;
                job.Error = null;
            }
            else if (best.Result.NeedsReview)
            {
                job.Status = DownloadJobStatus.NeedsReview;
                job.Error = $"Score {best.Result.Score:F2} requires review";
            }
            else
            {
                job.Status = DownloadJobStatus.Failed;
                job.Error = $"No confident match. Score {best.Result.Score:F2}";
            }

            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Job {JobId} finished with status {Status}, source {SourceUrl}, and score {MatchScore}.",
                job.Id,
                job.Status,
                job.SourceUrl,
                job.MatchScore);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed during candidate acquisition.", job.Id);
            Fail(job, ex.Message);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }

    private static void Fail(DownloadJob job, string error)
    {
        job.Status = DownloadJobStatus.Failed;
        job.Error = error;
        job.FinishedAt = DateTime.UtcNow;
    }
}

public record DownloadedFile
{
    public string FilePath { get; init; } = string.Empty;
    public byte[]? CoverArtBytes { get; init; }
}

/// <summary>
/// Interface for audio acquisition providers.
/// </summary>
public interface IAcquisitionProvider
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct);
    Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct);
}
