using MusicSync.Core;
using MusicSync.Infrastructure;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicSync.Worker.Interfaces;

namespace MusicSync.Worker;

/// <summary>
/// Background service that processes the download job queue from SQLite.
/// Handles the full pipeline: resolve candidates → download → tag → move to library.
/// Progress is published via Server-Sent Events (SSE) for connected clients.
/// </summary>
public class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;
    private readonly TimeSpan _processingDelay = TimeSpan.FromSeconds(1);

    // Channel for decoupling API enqueue from worker processing
    private readonly Channel<DownloadJob> _jobChannel = Channel.CreateBounded<DownloadJob>(1000);

    public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _ = Task.Run(() => ProcessQueueAsync(default));
    }

    /// <summary>
    /// Enqueues a download job for processing by the worker.
    /// </summary>
    public async Task EnqueueJobAsync(DownloadJob job)
    {
        await _jobChannel.Writer.WriteAsync(job);
        _logger.LogInformation("Job {JobId} enqueued for processing", job.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker starting.");

        try
        {
            await ProcessQueueAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker stopping.");
        }
    }

    private async IAsyncEnumerable<DownloadJob> ReadJobQueueAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var job = await _jobChannel.Reader.ReadAsync(cancellationToken);
            yield return job;

            await Task.Delay(_processingDelay, cancellationToken);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker queue processing started.");

        // Process jobs sequentially (concurrency=1 for rate-limiting safety)
        await foreach (var job in ReadJobQueueAsync(stoppingToken))
        {
            if (job is null) continue;

            await ProcessJobAsync(job, stoppingToken);
        }

        _logger.LogInformation("Worker queue processing completed.");
    }

    private async Task ProcessJobAsync(DownloadJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var contextDb = db as MusicSyncDbContext ?? new MusicSyncDbContext();

        // Set default status if new job
        job.Status ??= "Queued";
        contextDb.DownloadJobs.Attach(job);
        contextDb.Entry(job).Property(e => e.Status).IsModified = false;
        await contextDb.SaveChangesAsync(ct);

        _logger.LogInformation("Processing job {JobId}: Status={Status}", job.Id, job.Status);

        try
        {
            // Phase 1: Resolve/Match the track
            job.Status = "Resolving";
            await contextDb.SaveChangesAsync(ct);
            _logger.LogInformation("Job {JobId}: Moving to Resolving", job.Id);

            // Find candidates using the acquisition provider
            IAcquisitionProvider provider = GetProvider(job.ProviderName ?? "yt-dlp");
            var trackInfo = CreateTrackInfoFromJob(job);
            var candidates = await provider.FindCandidatesAsync(trackInfo, 5, ct);

            job.Status = "Searching";
            await contextDb.SaveChangesAsync(ct);

            if (candidates.Count == 0)
            {
                job.Status = "Failed";
                job.Error = "No matching candidates found";
                await contextDb.SaveChangesAsync(ct);
                _logger.LogWarning("Job {JobId}: No candidates found", job.Id);
                return;
            }

            // Phase 2: Score candidates using the match scorer
            IMatchScorer scorer = GetScorer();
            var best = candidates[0];
            var scoreResult = scorer.Score(trackInfo, best);
            _logger.LogInformation("Job {JobId}: Best candidate score {Score}, auto-accept: {AutoAccept}, needs review: {NeedsReview}",
                job.Id, scoreResult.Score, scoreResult.AutoAccept, scoreResult.NeedsReview);

            job.Status = "Searching";
            await contextDb.SaveChangesAsync(ct);

            if (candidates.Count == 0)
            {
                job.Status = "Failed";
                job.Error = "No matching candidates found";
                await contextDb.SaveChangesAsync(ct);
                _logger.LogWarning("Job {JobId}: No candidates found", job.Id);
                return;
            }

            // Phase 3: Determine fate based on scores
            if (scoreResult.AutoAccept)
            {
                // Auto-accept: proceed to download
                job.Status = "Downloading";
                await contextDb.SaveChangesAsync(ct);
                _logger.LogInformation("Job {JobId}: Auto-accepting candidate (score {Score} >= {Threshold})",
                    job.Id, scoreResult.Score, GetAutoAcceptThreshold());

                var downloaded = await provider.DownloadAsync(best, ct);

                // Phase 4: Tag the file with TagLibSharp
                job.Status = "Tagging";
                await contextDb.SaveChangesAsync(ct);

                ITagWriter tagWriter = GetTagWriter();
                await tagWriter.WriteAsync(downloaded.FilePath, trackInfo, downloaded.CoverArtBytes, ct);

                // Phase 5: Move to library path with proper multi-artist attribution
                job.Status = "MovingToLibrary";
                await contextDb.SaveChangesAsync(ct);

                var libraryPath = ResolveLibraryPath(trackInfo);
                var finalPath = System.IO.Path.GetFullPath(libraryPath);

                // Ensure directory exists
                var dir = System.IO.Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                // Move/save the file
                if (System.IO.File.Exists(downloaded.FilePath))
                {
                    System.IO.File.Move(downloaded.FilePath, finalPath, overwrite: true);
                }

                // Mark as done
                job.Status = "Done";
                job.OutputPath = finalPath;
                job.FinishedAt = DateTime.UtcNow;
                await contextDb.SaveChangesAsync(ct);

                _logger.LogInformation("Job {JobId}: Completed. File saved to {Path}", job.Id, finalPath);

                // Publish SSE progress
                await PublishProgressAsync(job.Id, "Done", 100, ct);
                return;
            }
            else if (scoreResult.NeedsReview)
            {
                // Park for review
                job.Status = "NeedsReview";
                job.Error = $"Score {scoreResult.Score:F2} requires review (above {GetReviewThreshold():F2})";
                await contextDb.SaveChangesAsync(ct);

                _logger.LogInformation("Job {JobId}: Parking for review (score {Score})",
                    job.Id, scoreResult.Score);

                await PublishProgressAsync(job.Id, "NeedsReview", (int)(scoreResult.Score * 100), ct);
                return;
            }
            else
            {
                // No confident match
                job.Status = "Failed";
                job.Error = $"No confident match. Score {scoreResult.Score:F2} < review threshold {GetReviewThreshold():F2}";
                await contextDb.SaveChangesAsync(ct);

                _logger.LogWarning("Job {JobId}: No confident match", job.Id);

                await PublishProgressAsync(job.Id, "Failed", 0, ct);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Job {JobId}: Unexpected error", job.Id);

            // Increment attempts and mark as failed if max retries exceeded
            job.Attempts++;
            if (job.Attempts >= 3) // max retries
            {
                job.Status = "Failed";
                job.Error = ex.Message;
                await contextDb.SaveChangesAsync(ct);
            }
            else
            {
                // Re-queue for retry with backoff
                job.Status = "Queued";
                await contextDb.SaveChangesAsync(ct);
                // Send back to channel for retry
                await _jobChannel.Writer.WriteAsync(job);
            }

            await PublishProgressAsync(job.Id, "Failed", 0, ct);
        }
    }

    // Helper methods for configuration and dependencies

    private TrackInfo CreateTrackInfoFromJob(DownloadJob job)
    {
        // TODO: Fetch Track entity from DB by job.TrackId and map to TrackInfo
        // For now, return a basic TrackInfo with placeholder values
        return new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Unknown Title",
            ArtistName = "Unknown Artist",
            DurationMs = 180000
        };
    }

    private string ResolveLibraryPath(TrackInfo trackInfo)
    {
        // Use LibraryPathResolver to build the proper path
        // {LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}
        var resolver = new LibraryPathResolver();
        return resolver.Resolve(trackInfo, asSingle: false, ".mp3");
    }

    private IAcquisitionProvider GetProvider(string name)
    {
        // Factory method to create the appropriate acquisition provider
        // Default to yt-dlp - will create YtDlpAcquisitionProvider next
        return new YtDlpAcquisitionProvider();
    }

    private IMatchScorer GetScorer()
    {
        // Return the match scorer instance - registered in DI
        // This will be resolved from the service provider in the actual implementation
        // For now, create a new instance with default thresholds
        return new TrackScorer(
            autoAcceptThreshold: GetAutoAcceptThreshold(),
            reviewThreshold: GetReviewThreshold(),
            durationToleranceSeconds: 5);
    }

    private ITagWriter GetTagWriter()
    {
        // Return the TagLibSharp tag writer instance - registered in DI
        throw new NotImplementedException("ITagWriter not yet configured in DI");
    }

    private double GetAutoAcceptThreshold()
    {
        return 0.80; // Matching__AutoAcceptThreshold
    }

    private double GetReviewThreshold()
    {
        return 0.50; // Matching__ReviewThreshold
    }

    private Task PublishProgressAsync(int jobId, string status, int progressPercentage, CancellationToken ct)
    {
        // Publish SSE progress event
        var progressEvent = JsonSerializer.Serialize(new { jobId = jobId, status = status, progress = progressPercentage });
        _logger.LogDebug("Publishing progress for job {JobId}: {Event}", jobId, progressEvent);
        // TODO: SignalR or actual SSE implementation would broadcast here
        return Task.CompletedTask;
    }
}

public record DownloadedFile
{
    public string FilePath { get; init; } = string.Empty;
    public byte[]? CoverArtBytes { get; init; }
}

/// <summary>
/// Interface for audio acquisition providers (yt-dlp, etc.)
/// </summary>
public interface IAcquisitionProvider
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct);
    Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct);
}
