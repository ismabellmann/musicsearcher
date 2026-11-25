using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/library")]
public sealed class LibraryController : ControllerBase
{
    private readonly LibraryImportService _imports;
    private readonly LibraryFileService _files;
    private readonly MusicSyncDbContext _db;

    public LibraryController(LibraryImportService imports, LibraryFileService files, MusicSyncDbContext db)
    {
        _imports = imports;
        _files = files;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<LibraryFileSummary>>>> ListFiles(
        string? q,
        string? origin,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(origin)
            && !string.Equals(origin, "imported", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(origin, "downloaded", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse<PagedResult<LibraryFileSummary>>.Failure(
                StatusCodes.Status400BadRequest, "El origen debe ser imported o downloaded."));
        }

        var result = await _files.ListAsync(q, origin, page, pageSize, ct);
        var data = new PagedResult<LibraryFileSummary>(
            result.Items.Select(ToSummary).ToList(), result.Page, result.PageSize, result.TotalCount);
        return Ok(ApiResponse<PagedResult<LibraryFileSummary>>.Success(
            StatusCodes.Status200OK, "Archivos de la biblioteca consultados.", data));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<LibraryFileDetail>>> GetFile(int id, CancellationToken ct)
    {
        var file = await _files.GetAsync(id, ct);
        return file is null
            ? NotFound(ApiResponse<LibraryFileDetail>.Failure(StatusCodes.Status404NotFound, "No se encontró el archivo."))
            : Ok(ApiResponse<LibraryFileDetail>.Success(StatusCodes.Status200OK, "Archivo de biblioteca consultado.", ToDetail(file)));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteFile(int id, CancellationToken ct)
    {
        var outcome = await _files.DeleteAsync(id, ct);
        return outcome switch
        {
            DeleteLibraryFileOutcome.Deleted => Ok(ApiResponse<object>.Success(
                StatusCodes.Status200OK, "Archivo eliminado de la biblioteca.", new object())),
            DeleteLibraryFileOutcome.NotFound => NotFound(ApiResponse<object>.Failure(
                StatusCodes.Status404NotFound, "No se encontró el archivo.")),
            _ => BadRequest(ApiResponse<object>.Failure(
                StatusCodes.Status400BadRequest, "La ruta almacenada no corresponde a un archivo válido."))
        };
    }

    private static LibraryFileSummary ToSummary(LibraryFileView file) => new(
        file.Id, file.RelativePath, file.Title, file.ArtistName, file.AlbumTitle,
        file.Format, file.SizeBytes, file.AddedAt, file.Origin, file.MusicBrainzRating, file.PersonalRating, file.Genre);

    private static LibraryFileDetail ToDetail(LibraryFileView file) => new(
        file.Id, file.RelativePath, file.RecordingMbid, file.Title, file.ArtistName,
        file.AlbumTitle, file.ReleaseGroupMbid, file.Year, file.Isrc, file.DurationMs,
        file.Format, file.Bitrate, file.SizeBytes, file.AddedAt,
        file.Origin, file.SourceUrl, file.Artists, file.MusicBrainzRating, file.PersonalRating, file.PersonalNotes, file.Genre);

    [HttpPost("scan")]
    public async Task<ActionResult<ApiResponse<ScanSummary>>> Scan(CancellationToken ct)
    {
        try
        {
            var summary = await _imports.ScanAsync(ct);
            return Ok(ApiResponse<ScanSummary>.Success(
                StatusCodes.Status200OK,
                $"Escaneo completado: {summary.Added} nuevos, {summary.AlreadyKnown} ya conocidos, {summary.Unrecognized} no reconocidos.",
                summary));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ScanSummary>.Failure(StatusCodes.Status400BadRequest, ex.Message));
        }
    }

    [HttpGet("imports")]
    public async Task<ActionResult<ApiResponse<PagedResult<LibraryImportSummary>>>> List(
        string? status,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = _db.LibraryImports.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(item => item.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new LibraryImportSummary(
                item.Id, item.RelativePath, item.ArtistName, item.Title, item.AlbumTitle,
                item.Status, item.DurationMs, item.TrackId, item.Error, item.IdentifiedAt))
            .ToListAsync(ct);

        return Ok(ApiResponse<PagedResult<LibraryImportSummary>>.Success(
            StatusCodes.Status200OK,
            "Archivos de la biblioteca consultados.",
            new PagedResult<LibraryImportSummary>(items, page, pageSize, total)));
    }

    [HttpGet("imports/{id:int}")]
    public async Task<ActionResult<ApiResponse<LibraryImportDetail>>> Get(int id, CancellationToken ct)
    {
        var detail = await _db.LibraryImports.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new LibraryImportDetail(
                item.Id, item.RelativePath, item.ArtistName, item.Title, item.AlbumTitle,
                item.Status, item.DurationMs, item.TrackId, item.Error, item.IdentifiedAt,
                item.SourceUrl,
                item.Candidates
                    .OrderBy(candidate => candidate.Rank)
                    .Select(candidate => new LibraryImportCandidateDto(
                        candidate.Id, candidate.Rank, candidate.RecordingMbid, candidate.Title,
                        candidate.ArtistsJson, candidate.AlbumTitle, candidate.ReleaseGroupMbid,
                        candidate.Year, candidate.DurationMs, candidate.Score, candidate.Reason,
                        candidate.Provider, candidate.SourceUrl))
                    .ToList()))
            .SingleOrDefaultAsync(ct);

        if (detail is null)
            return NotFound(ApiResponse<LibraryImportDetail>.Failure(StatusCodes.Status404NotFound, "No se encontró el archivo."));

        return Ok(ApiResponse<LibraryImportDetail>.Success(StatusCodes.Status200OK, "Archivo consultado.", detail));
    }

    [HttpPost("imports/identify")]
    public async Task<ActionResult<ApiResponse<object>>> Identify(IdentifyRequest? request, CancellationToken ct)
    {
        var processed = await _imports.IdentifyAsync(request?.ImportIds, ct);
        return Ok(ApiResponse<object>.Success(
            StatusCodes.Status200OK,
            $"Identificación completada para {processed} archivo(s).",
            new { processed }));
    }

    [HttpPost("covers")]
    public async Task<ActionResult<ApiResponse<CoverBackfillSummary>>> BackfillCovers(
        CoverBackfillRequest? request,
        CancellationToken ct)
    {
        var summary = await _imports.BackfillCoversAsync(request?.ImportIds, ct);
        return Ok(ApiResponse<CoverBackfillSummary>.Success(
            StatusCodes.Status200OK,
            $"Portadas procesadas: {summary.Embedded} incrustadas, {summary.Skipped} omitidas, {summary.Failed} fallidas.",
            summary));
    }

    [HttpPost("imports/{id:int}/accept")]
    public async Task<ActionResult<ApiResponse<object>>> Accept(int id, AcceptRequest request, CancellationToken ct)
    {
        var outcome = await _imports.AcceptCandidateAsync(id, request.CandidateId, ct);
        return OutcomeResult(outcome, "Coincidencia aceptada. El archivo quedó registrado en la biblioteca.");
    }

    [HttpPost("imports/{id:int}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(int id, CancellationToken ct)
    {
        var outcome = await _imports.RejectAsync(id, ct);
        return OutcomeResult(outcome, "Archivo descartado.");
    }

    [HttpPut("imports/{id:int}/path")]
    public async Task<ActionResult<ApiResponse<object>>> UpdatePath(int id, PathRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await _imports.UpdatePathAsync(id, request.RelativePath ?? string.Empty, ct);
            return OutcomeResult(outcome, "Ruta actualizada.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Failure(StatusCodes.Status400BadRequest, ex.Message));
        }
    }

    private ActionResult<ApiResponse<object>> OutcomeResult(AcceptOutcome outcome, string successMessage)
    {
        return outcome switch
        {
            AcceptOutcome.Imported or AcceptOutcome.Rejected or AcceptOutcome.Updated =>
                Ok(ApiResponse<object>.Success(StatusCodes.Status200OK, successMessage, new object())),
            AcceptOutcome.ImportNotFound =>
                NotFound(ApiResponse<object>.Failure(StatusCodes.Status404NotFound, "No se encontró el archivo.")),
            AcceptOutcome.CandidateNotFound =>
                BadRequest(ApiResponse<object>.Failure(StatusCodes.Status400BadRequest, "La coincidencia indicada no pertenece a este archivo.")),
            AcceptOutcome.AlreadyImported =>
                Conflict(ApiResponse<object>.Failure(StatusCodes.Status409Conflict, "Este archivo ya fue importado.")),
            AcceptOutcome.RecordingAlreadyInLibrary =>
                Conflict(ApiResponse<object>.Failure(StatusCodes.Status409Conflict, "Esta grabación ya tiene un archivo en la biblioteca.")),
            AcceptOutcome.FileAlreadyInLibrary =>
                Conflict(ApiResponse<object>.Failure(StatusCodes.Status409Conflict, "Este archivo ya existe en la biblioteca (misma huella SHA-256).")),
            AcceptOutcome.FileMissing =>
                BadRequest(ApiResponse<object>.Failure(StatusCodes.Status400BadRequest, "El archivo no existe en la carpeta de la biblioteca.")),
            AcceptOutcome.PathInUse =>
                Conflict(ApiResponse<object>.Failure(StatusCodes.Status409Conflict, "Otro registro ya usa esa ruta.")),
            _ => BadRequest(ApiResponse<object>.Failure(StatusCodes.Status400BadRequest, "La operación no es válida."))
        };
    }
}

public sealed record IdentifyRequest(int[]? ImportIds);

public sealed record CoverBackfillRequest(int[]? ImportIds);

public sealed record AcceptRequest(int CandidateId);

public sealed record PathRequest(string? RelativePath);

public sealed record LibraryImportSummary(
    int ImportId,
    string RelativePath,
    string ArtistName,
    string Title,
    string? AlbumTitle,
    string Status,
    int? DurationMs,
    int? TrackId,
    string? Error,
    DateTime? IdentifiedAt);

public sealed record LibraryImportDetail(
    int ImportId,
    string RelativePath,
    string ArtistName,
    string Title,
    string? AlbumTitle,
    string Status,
    int? DurationMs,
    int? TrackId,
    string? Error,
    DateTime? IdentifiedAt,
    string? SourceUrl,
    IReadOnlyList<LibraryImportCandidateDto> Candidates);

public sealed record LibraryImportCandidateDto(
    int CandidateId,
    int Rank,
    Guid RecordingMbid,
    string Title,
    string ArtistsJson,
    string? AlbumTitle,
    Guid? ReleaseGroupMbid,
    int? Year,
    int? DurationMs,
    float Score,
    string? Reason,
    string Provider,
    string SourceUrl);

public sealed record LibraryFileSummary(
    int FileId,
    string RelativePath,
    string Title,
    string ArtistName,
    string? AlbumTitle,
    string? Format,
    long SizeBytes,
    DateTime AddedAt,
    string Origin,
    double? MusicBrainzRating,
    int PersonalRating,
    string? Genre);

public sealed record LibraryFileDetail(
    int FileId,
    string RelativePath,
    Guid RecordingMbid,
    string Title,
    string ArtistName,
    string? AlbumTitle,
    Guid? ReleaseGroupMbid,
    int? Year,
    string? Isrc,
    int DurationMs,
    string? Format,
    int? Bitrate,
    long SizeBytes,
    DateTime AddedAt,
    string Origin,
    string? SourceUrl,
    IReadOnlyList<string> Artists,
    double? MusicBrainzRating,
    int PersonalRating,
    string? PersonalNotes,
    string? Genre);
