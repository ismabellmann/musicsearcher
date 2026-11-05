using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

namespace MusicSync.Worker;

/// <summary>
/// Processes persisted download jobs. Queued jobs are matched to the best candidate and then
/// marked ReadyForTagging; ready and approved jobs are downloaded, transcoded, stored in the library, and marked Done.
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const string DefaultProviderName = "deterministic";
    private const int MaxCandidates = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;
    private readonly IReadOnlyDictionary<string, IAcquisitionProvider> _providers;
    private readonly IMatchScorer _matchScorer;
    private readonly LibraryPathResolver _pathResolver;
    private readonly FfmpegAudioTranscoder _transcoder;
    private readonly ITagWriter _tagWriter;
    private readonly ICoverArtProvider _coverArt;
    private readonly LibraryOptions _library;
    private readonly RetryPolicy _retryPolicy;

    public Worker(
        IServiceScopeFactory scopeFactory,
        ILogger<Worker> logger,
        IEnumerable<IAcquisitionProvider> providers,
        IMatchScorer matchScorer,
        LibraryPathResolver pathResolver,
        FfmpegAudioTranscoder transcoder,
        ITagWriter tagWriter,
        ICoverArtProvider coverArt,
        LibraryOptions library,
        WorkerOptions? workerOptions = null)
    {
        var options = workerOptions ?? new WorkerOptions();
        _retryPolicy = new RetryPolicy(options.MaxAutomaticRetries, TimeSpan.FromSeconds(options.RetryBaseDelaySeconds));

        _scopeFactory = scopeFactory;
        _logger = logger;
        _providers = providers.ToDictionary(provider => provider.Name, StringComparer.Ordinal);
        _matchScorer = matchScorer;
        _pathResolver = pathResolver;
        _transcoder = transcoder;
        _tagWriter = tagWriter;
        _coverArt = coverArt;
        _library = library;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download worker started.");
        await RecoverInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var matched = await ProcessNextJobAsync(stoppingToken);
                var stored = await ProcessNextReadyJobAsync(stoppingToken);
                if (!matched && !stored)
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

        await db.DownloadJobs
            .Where(job => job.Status == DownloadJobStatus.Downloading || job.Status == DownloadJobStatus.Tagging)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(job => job.Status, DownloadJobStatus.ReadyForTagging),
                ct);
    }

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var jobId = await db.DownloadJobs
            .Where(item => item.Status == DownloadJobStatus.Queued
                && (item.NextAttemptAt == null || item.NextAttemptAt <= DateTime.UtcNow))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => (int?)item.Id)
            .FirstOrDefaultAsync(ct);

        if (jobId is null)
            return false;

        var startedAt = DateTime.UtcNow;
        var claimed = await db.DownloadJobs
            .Where(item => item.Id == jobId.Value
                && item.Status == DownloadJobStatus.Queued
                && (item.NextAttemptAt == null || item.NextAttemptAt <= DateTime.UtcNow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, DownloadJobStatus.Resolving)
                    .SetProperty(item => item.StartedAt, item => item.StartedAt ?? startedAt)
                    .SetProperty(item => item.Attempts, item => item.Attempts + 1)
                    .SetProperty(item => item.Error, (string?)null)
                    .SetProperty(item => item.NextAttemptAt, (DateTime?)null),
                ct);

        if (claimed == 0)
            return true;

        var job = await db.DownloadJobs.SingleAsync(item => item.Id == jobId.Value, ct);

        try
        {
            var provider = ResolveProvider(job.ProviderName);

            var track = await TrackCatalog.FindWithArtistsAsync(db, job.TrackId, ct);
            if (track is null)
            {
                Fail(job, $"Track {job.TrackId} was not found");
                await db.SaveChangesAsync(ct);
                return true;
            }

            var trackInfo = ToTrackInfo(track);

            job.Status = DownloadJobStatus.Searching;
            await db.SaveChangesAsync(ct);

            var candidates = await provider.FindCandidatesAsync(trackInfo, MaxCandidates, ct);
            if (candidates.Count == 0)
            {
                Fail(job, "No matching candidates found");
                await db.SaveChangesAsync(ct);
                return true;
            }

            var ranked = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Result = _matchScorer.Score(trackInfo, candidate)
                })
                .OrderByDescending(item => item.Result.Score)
                .Take(MaxCandidates)
                .ToList();

            await db.DownloadJobCandidates
                .Where(item => item.DownloadJobId == job.Id)
                .ExecuteDeleteAsync(ct);

            for (var index = 0; index < ranked.Count; index++)
            {
                var item = ranked[index];
                db.DownloadJobCandidates.Add(new DownloadJobCandidate
                {
                    DownloadJobId = job.Id,
                    Rank = index + 1,
                    SourceUrl = Limit(item.Candidate.SourceUrl, 500),
                    Title = Limit(item.Candidate.Title, 500),
                    DurationMs = item.Candidate.DurationMs,
                    Score = (float)item.Result.Score,
                    Reason = Limit(item.Result.Reason, 500)
                });
            }

            var best = ranked[0];
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
            await HandleFailureAsync(db, job, ex, DownloadJobStatus.Queued, "candidate acquisition", ct);
            return true;
        }
    }

    private async Task<bool> ProcessNextReadyJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var jobId = await db.DownloadJobs
            .Where(item => (item.Status == DownloadJobStatus.ReadyForTagging || item.Status == DownloadJobStatus.Approved)
                && (item.NextAttemptAt == null || item.NextAttemptAt <= DateTime.UtcNow))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => (int?)item.Id)
            .FirstOrDefaultAsync(ct);

        if (jobId is null)
            return false;

        var claimed = await db.DownloadJobs
            .Where(item => item.Id == jobId.Value
                && (item.Status == DownloadJobStatus.ReadyForTagging || item.Status == DownloadJobStatus.Approved)
                && (item.NextAttemptAt == null || item.NextAttemptAt <= DateTime.UtcNow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, DownloadJobStatus.Downloading)
                    .SetProperty(item => item.NextAttemptAt, (DateTime?)null),
                ct);

        if (claimed == 0)
            return true;

        var job = await db.DownloadJobs.SingleAsync(item => item.Id == jobId.Value, ct);
        string? stagedSource = null;
        string? stagedMp3 = null;
        string? movedTarget = null;
        string? temporaryDirectory = null;

        try
        {
            var provider = ResolveProvider(job.ProviderName);
            var track = await TrackCatalog.FindWithArtistsAsync(db, job.TrackId, ct);
            if (track is null)
            {
                Fail(job, $"Track {job.TrackId} was not found");
                await db.SaveChangesAsync(ct);
                return true;
            }

            if (string.IsNullOrWhiteSpace(job.SourceUrl))
            {
                Fail(job, "No hay un origen seleccionado para este trabajo.");
                await db.SaveChangesAsync(ct);
                return true;
            }

            var trackInfo = ToTrackInfo(track);
            var libraryRoot = Path.GetFullPath(_library.Root);
            var stagingDirectory = Path.GetFullPath(_library.StagingDirectory);
            Directory.CreateDirectory(stagingDirectory);

            var downloaded = await provider.DownloadAsync(
                new SourceCandidate { Title = track.Title, SourceUrl = job.SourceUrl },
                ct);

            if (string.IsNullOrEmpty(downloaded.FilePath))
                throw new InvalidOperationException("El proveedor no entregó un archivo de audio.");

            if (downloaded.IsTemporary)
                temporaryDirectory = Path.GetDirectoryName(downloaded.FilePath);

            stagedSource = Path.Combine(stagingDirectory, $"{job.Id}.source{Path.GetExtension(downloaded.FilePath)}");
            File.Copy(downloaded.FilePath, stagedSource, overwrite: true);

            job.Status = DownloadJobStatus.Tagging;
            await db.SaveChangesAsync(ct);

            stagedMp3 = Path.Combine(stagingDirectory, $"{job.Id}.mp3");
            await _transcoder.TranscodeToMp3Async(stagedSource, stagedMp3, trackInfo, ct);

            var coverArt = await _coverArt.GetFrontCoverAsync(trackInfo.ReleaseGroupMbid ?? Guid.Empty, ct);
            await _tagWriter.WriteAsync(stagedMp3, trackInfo, coverArt, ct);

            var target = _pathResolver.ResolveWithinRoot(libraryRoot, trackInfo, asSingle: true, ".mp3");
            if (File.Exists(target))
            {
                job.Status = DownloadJobStatus.Done;
                job.Error = "AlreadyExists";
                job.OutputPath = target;
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Job {JobId} skipped: {Path} already exists.", job.Id, target);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(stagedMp3, target, overwrite: false);
            movedTarget = target;

            db.LibraryFiles.Add(new LibraryFile
            {
                TrackId = job.TrackId,
                RelativePath = Path.GetRelativePath(libraryRoot, target),
                Format = "mp3",
                SizeBytes = new FileInfo(target).Length,
                Sha256 = await ComputeSha256Async(target, ct),
                AddedAt = DateTime.UtcNow
            });

            job.Status = DownloadJobStatus.Done;
            job.Error = null;
            job.OutputPath = target;
            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            movedTarget = null;

            _logger.LogInformation("Job {JobId} stored at {Path}.", job.Id, target);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed while storing the file.", job.Id);

            if (movedTarget is not null && File.Exists(movedTarget))
                File.Delete(movedTarget);

            db.ChangeTracker.Clear();
            var failed = await db.DownloadJobs.SingleAsync(item => item.Id == jobId.Value, CancellationToken.None);
            await HandleFailureAsync(db, failed, ex, DownloadJobStatus.ReadyForTagging, "storage", CancellationToken.None);
            return true;
        }
        finally
        {
            DeleteIfExists(stagedSource);
            DeleteIfExists(stagedMp3);
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private IAcquisitionProvider ResolveProvider(string? name)
    {
        var key = string.IsNullOrEmpty(name) ? DefaultProviderName : name;
        if (!_providers.TryGetValue(key, out var provider))
            throw new InvalidOperationException($"Acquisition provider '{key}' is not registered.");

        return provider;
    }

    private static TrackInfo ToTrackInfo(Track track) => TrackCatalog.ToTrackInfo(track);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Limit(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
            File.Delete(path);
    }

    private async Task HandleFailureAsync(
        MusicSyncDbContext db,
        DownloadJob job,
        Exception ex,
        string retryStatus,
        string stage,
        CancellationToken ct)
    {
        if (TransientErrors.IsTransient(ex))
        {
            if (job.AutomaticRetries < _retryPolicy.MaxAutomaticRetries)
            {
                job.AutomaticRetries++;
                job.Status = retryStatus;
                job.NextAttemptAt = DateTime.UtcNow + _retryPolicy.DelayForRetry(job.AutomaticRetries);
                job.FinishedAt = null;
                job.Error = Limit(
                    $"Transient {stage} error; automatic retry {job.AutomaticRetries} of {_retryPolicy.MaxAutomaticRetries}: {ex.Message}",
                    500);
                _logger.LogWarning(
                    "Job {JobId} will retry {Stage} at {NextAttemptAt} after a transient error.",
                    job.Id, stage, job.NextAttemptAt);
            }
            else
            {
                Fail(job, Limit($"Failed after {job.AutomaticRetries + 1} attempts ({stage}): {ex.Message}", 500));
            }
        }
        else
        {
            Fail(job, Limit(ex.Message, 500));
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Fail(DownloadJob job, string error)
    {
        job.Status = DownloadJobStatus.Failed;
        job.Error = error;
        job.FinishedAt = DateTime.UtcNow;
        job.NextAttemptAt = null;
    }
}

public record DownloadedFile
{
    public string FilePath { get; init; } = string.Empty;
    public byte[]? CoverArtBytes { get; init; }

    /// <summary>True when the provider created FilePath inside its own work directory, which the worker removes after use.</summary>
    public bool IsTemporary { get; init; }
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
