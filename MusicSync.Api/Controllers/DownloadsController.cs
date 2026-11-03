using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Infrastructure;
using MusicSync.Worker;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/downloads")]
public sealed class DownloadsController : ControllerBase
{
    private readonly MusicSyncDbContext _db;
    private readonly IReadOnlyCollection<string> _providerNames;

    public DownloadsController(MusicSyncDbContext db, IEnumerable<IAcquisitionProvider> providers)
    {
        _db = db;
        _providerNames = providers.Select(provider => provider.Name).ToList();
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<DownloadJobCreated>>> Create(
        CreateDownloadRequest request,
        CancellationToken ct)
    {
        if (request.Track is null
            || request.Track.RecordingMbid == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Track.Title))
        {
            return BadRequest(ApiResponse<DownloadJobCreated>.Failure(
                StatusCodes.Status400BadRequest,
                "Se requiere un identificador de MusicBrainz y el título de la canción."));
        }

        var providerName = string.IsNullOrWhiteSpace(request.Provider) ? "deterministic" : request.Provider.Trim();
        if (!_providerNames.Contains(providerName, StringComparer.Ordinal))
        {
            return BadRequest(ApiResponse<DownloadJobCreated>.Failure(
                StatusCodes.Status400BadRequest,
                "El proveedor de adquisición indicado no está disponible."));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var track = await TrackCatalog.GetOrCreateTrackAsync(_db, request.Track, ct);

        var job = new DownloadJob
        {
            TrackId = track.Id,
            Status = DownloadJobStatus.Queued,
            ProviderName = providerName
        };
        _db.DownloadJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Accepted(ApiResponse<DownloadJobCreated>.Success(
            StatusCodes.Status202Accepted,
            "Canción añadida a la cola exitosamente.",
            new DownloadJobCreated(job.Id, job.Status)));
    }

    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DownloadJobSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DownloadJobSummary>>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<DownloadJobSummary>>>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || page > int.MaxValue / pageSize)
        {
            return BadRequest(ApiResponse<PagedResult<DownloadJobSummary>>.Failure(
                StatusCodes.Status400BadRequest,
                "La página debe ser mayor que cero y pageSize debe estar entre 1 y 100."));
        }

        var normalizedStatus = NormalizeStatus(status);
        if (status is not null && normalizedStatus is null)
        {
            return BadRequest(ApiResponse<PagedResult<DownloadJobSummary>>.Failure(
                StatusCodes.Status400BadRequest,
                "El estado solicitado no es válido."));
        }

        var query = _db.DownloadJobs.AsNoTracking();
        if (normalizedStatus is not null)
            query = query.Where(job => job.Status == normalizedStatus);

        var totalCount = await query.CountAsync(ct);
        var jobsQuery = normalizedStatus == DownloadJobStatus.Queued
            ? query.OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)
            : query.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id);

        var jobs = await jobsQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(job => new DownloadJobSummary(
                job.Id,
                job.Status,
                job.TrackId,
                _db.Tracks.Where(track => track.Id == job.TrackId)
                    .Select(track => track.Title)
                    .FirstOrDefault() ?? string.Empty,
                _db.Tracks.Where(track => track.Id == job.TrackId)
                    .Select(track => track.ArtistName)
                    .FirstOrDefault() ?? string.Empty,
                job.CreatedAt,
                job.StartedAt,
                job.FinishedAt,
                job.Attempts,
                job.Error,
                job.ProviderName,
                job.SourceUrl,
                job.MatchScore,
                job.Status == DownloadJobStatus.Queued
                    ? _db.DownloadJobs.Count(earlier =>
                        earlier.Status == DownloadJobStatus.Queued
                        && (earlier.CreatedAt < job.CreatedAt
                            || (earlier.CreatedAt == job.CreatedAt && earlier.Id <= job.Id)))
                    : null,
                job.NextAttemptAt,
                null))
            .ToListAsync(ct);

        var result = new PagedResult<DownloadJobSummary>(jobs, page, pageSize, totalCount);
        return Ok(ApiResponse<PagedResult<DownloadJobSummary>>.Success(
            StatusCodes.Status200OK,
            "Trabajos de descarga consultados exitosamente.",
            result));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DownloadJobSummary>>> Get(int id, CancellationToken ct)
    {
        var job = await FindJobSummaryAsync(id, ct, includeCandidates: true);
        if (job is null)
        {
            return NotFound(ApiResponse<DownloadJobSummary>.Failure(
                StatusCodes.Status404NotFound,
                "No se encontró el trabajo de descarga."));
        }

        return Ok(ApiResponse<DownloadJobSummary>.Success(
            StatusCodes.Status200OK,
            "Estado del trabajo consultado exitosamente.",
            job));
    }

    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<DownloadJobSummary>>> Approve(
        int id,
        [FromBody] ApproveDownloadRequest? request,
        CancellationToken ct)
    {
        int approved;
        if (request?.CandidateId is int candidateId)
        {
            var candidate = await _db.DownloadJobCandidates
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == candidateId && item.DownloadJobId == id, ct);

            if (candidate is null)
            {
                return BadRequest(ApiResponse<DownloadJobSummary>.Failure(
                    StatusCodes.Status400BadRequest,
                    "El candidato indicado no pertenece a este trabajo."));
            }

            approved = await _db.DownloadJobs
                .Where(job => job.Id == id && job.Status == DownloadJobStatus.NeedsReview)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.Status, DownloadJobStatus.Approved)
                        .SetProperty(job => job.Error, (string?)null)
                        .SetProperty(job => job.FinishedAt, (DateTime?)null)
                        .SetProperty(job => job.SourceUrl, candidate.SourceUrl)
                        .SetProperty(job => job.MatchScore, (float?)candidate.Score),
                    ct);
        }
        else
        {
            approved = await _db.DownloadJobs
                .Where(job => job.Id == id && job.Status == DownloadJobStatus.NeedsReview)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.Status, DownloadJobStatus.Approved)
                        .SetProperty(job => job.Error, (string?)null)
                        .SetProperty(job => job.FinishedAt, (DateTime?)null),
                    ct);
        }

        if (approved == 0)
        {
            return await ReturnUnavailableJobAsync(
                id,
                "Solo se pueden aprobar trabajos que estén en revisión.",
                ct);
        }

        var job = await FindJobSummaryAsync(id, ct);
        return Ok(ApiResponse<DownloadJobSummary>.Success(
            StatusCodes.Status200OK,
            "Candidato aprobado. La descarga se iniciará en la cola.",
            job!));
    }

    [HttpPost("{id:int}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<DownloadJobSummary>>> Cancel(int id, CancellationToken ct)
    {
        var cancelled = await _db.DownloadJobs
            .Where(job => job.Id == id
                && (job.Status == DownloadJobStatus.Queued
                    || job.Status == DownloadJobStatus.NeedsReview))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, DownloadJobStatus.Cancelled)
                    .SetProperty(job => job.Error, "Cancelado por el usuario.")
                    .SetProperty(job => job.FinishedAt, DateTime.UtcNow),
                ct);

        if (cancelled == 0)
            return await ReturnUnavailableJobAsync(id, "Solo se pueden cancelar trabajos en cola o en revisión.", ct);

        var job = await FindJobSummaryAsync(id, ct);
        return Ok(ApiResponse<DownloadJobSummary>.Success(
            StatusCodes.Status200OK,
            "Trabajo cancelado exitosamente.",
            job!));
    }

    [HttpPost("{id:int}/retry")]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<DownloadJobSummary>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<DownloadJobSummary>>> Retry(int id, CancellationToken ct)
    {
        var retried = await _db.DownloadJobs
            .Where(job => job.Id == id && job.Status == DownloadJobStatus.Failed)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, DownloadJobStatus.Queued)
                    .SetProperty(job => job.Error, (string?)null)
                    .SetProperty(job => job.FinishedAt, (DateTime?)null)
                    .SetProperty(job => job.SourceUrl, (string?)null)
                    .SetProperty(job => job.MatchScore, (float?)null)
                    .SetProperty(job => job.AutomaticRetries, 0)
                    .SetProperty(job => job.NextAttemptAt, (DateTime?)null),
                ct);

        if (retried == 0)
            return await ReturnUnavailableJobAsync(id, "Solo se pueden reintentar trabajos fallidos.", ct);

        var job = await FindJobSummaryAsync(id, ct);
        return Accepted(ApiResponse<DownloadJobSummary>.Success(
            StatusCodes.Status202Accepted,
            "Trabajo añadido nuevamente a la cola.",
            job!));
    }

    private async Task<ActionResult<ApiResponse<DownloadJobSummary>>> ReturnUnavailableJobAsync(
        int id,
        string conflictMessage,
        CancellationToken ct)
    {
        var status = await _db.DownloadJobs
            .AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => job.Status)
            .SingleOrDefaultAsync(ct);

        if (status is null)
        {
            return NotFound(ApiResponse<DownloadJobSummary>.Failure(
                StatusCodes.Status404NotFound,
                "No se encontró el trabajo de descarga."));
        }

        return Conflict(ApiResponse<DownloadJobSummary>.Failure(
            StatusCodes.Status409Conflict,
            $"{conflictMessage} Estado actual: {status}."));
    }

    private async Task<DownloadJobSummary?> FindJobSummaryAsync(int id, CancellationToken ct, bool includeCandidates = false)
    {
        var summary = await _db.DownloadJobs
            .AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => new DownloadJobSummary(
                job.Id,
                job.Status,
                job.TrackId,
                _db.Tracks.Where(track => track.Id == job.TrackId)
                    .Select(track => track.Title)
                    .FirstOrDefault() ?? string.Empty,
                _db.Tracks.Where(track => track.Id == job.TrackId)
                    .Select(track => track.ArtistName)
                    .FirstOrDefault() ?? string.Empty,
                job.CreatedAt,
                job.StartedAt,
                job.FinishedAt,
                job.Attempts,
                job.Error,
                job.ProviderName,
                job.SourceUrl,
                job.MatchScore,
                job.Status == DownloadJobStatus.Queued
                    ? _db.DownloadJobs.Count(earlier =>
                        earlier.Status == DownloadJobStatus.Queued
                        && (earlier.CreatedAt < job.CreatedAt
                            || (earlier.CreatedAt == job.CreatedAt && earlier.Id <= job.Id)))
                    : null,
                job.NextAttemptAt,
                null))
            .SingleOrDefaultAsync(ct);

        if (summary is null || !includeCandidates)
            return summary;

        var candidates = await _db.DownloadJobCandidates
            .AsNoTracking()
            .Where(item => item.DownloadJobId == id)
            .OrderBy(item => item.Rank)
            .Select(item => new DownloadJobCandidateDto(
                item.Id,
                item.Rank,
                item.SourceUrl,
                item.Title,
                item.DurationMs,
                item.Score,
                item.Reason))
            .ToListAsync(ct);

        return summary with { Candidates = candidates };
    }

    private static string? NormalizeStatus(string? status)
    {
        if (status is null)
            return null;

        return DownloadJobStatus.All.FirstOrDefault(
            knownStatus => string.Equals(knownStatus, status, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record CreateDownloadRequest(TrackInfo Track, string? Provider = null);

public sealed record DownloadJobCreated(int JobId, string JobStatus);

public sealed record DownloadJobSummary(
    int JobId,
    string JobStatus,
    int TrackId,
    string TrackTitle,
    string ArtistName,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int Attempts,
    string? Error,
    string? ProviderName,
    string? SourceUrl,
    float? MatchScore,
    int? QueuePosition,
    DateTime? NextAttemptAt,
    IReadOnlyList<DownloadJobCandidateDto>? Candidates = null);

public sealed record DownloadJobCandidateDto(
    int CandidateId,
    int Rank,
    string SourceUrl,
    string Title,
    int? DurationMs,
    float Score,
    string? Reason);

public sealed record ApproveDownloadRequest(int? CandidateId = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
