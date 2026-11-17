using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/playlists")]
public sealed class PlaylistController : ControllerBase
{
    private readonly PlaylistImportService _imports;

    public PlaylistController(PlaylistImportService imports)
    {
        _imports = imports;
    }

    [HttpPost("preview")]
    public async Task<ActionResult<ApiResponse<PlaylistPreviewResult>>> Preview(
        PlaylistPreviewRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _imports.PreviewAsync(request.Url, ct);
            return Ok(ApiResponse<PlaylistPreviewResult>.Success(
                StatusCodes.Status200OK, "Vista previa de playlist creada.", result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status400BadRequest, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status400BadRequest, ex.Message));
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<PlaylistPreviewResult>>> Get(int id, CancellationToken ct)
    {
        var result = await _imports.GetAsync(id, ct);
        return result is null
            ? NotFound(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status404NotFound, "No se encontró la importación de playlist."))
            : Ok(ApiResponse<PlaylistPreviewResult>.Success(StatusCodes.Status200OK, "Importación de playlist consultada.", result));
    }

    [HttpPost("album/{mbid:guid}/preview")]
    public async Task<ActionResult<ApiResponse<PlaylistPreviewResult>>> PreviewAlbum(Guid mbid, CancellationToken ct)
    {
        var result = await _imports.PreviewAlbumAsync(mbid, ct);
        return result is null
            ? NotFound(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status404NotFound, "No se encontró el álbum."))
            : Ok(ApiResponse<PlaylistPreviewResult>.Success(StatusCodes.Status200OK, "Vista previa del álbum creada.", result));
    }

    [HttpPost("artist/{mbid:guid}/preview")]
    public async Task<ActionResult<ApiResponse<PlaylistPreviewResult>>> PreviewArtist(
        Guid mbid,
        string artistName,
        int count = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return BadRequest(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status400BadRequest, "El nombre del artista es obligatorio."));

        try
        {
            var result = await _imports.PreviewArtistAsync(mbid, artistName, count, ct);
            return Ok(ApiResponse<PlaylistPreviewResult>.Success(StatusCodes.Status200OK, "Vista previa del artista creada.", result!));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PlaylistPreviewResult>.Failure(StatusCodes.Status400BadRequest, ex.Message));
        }
    }

    [HttpPost("{id:int}/enqueue")]
    public async Task<ActionResult<ApiResponse<PlaylistEnqueueResult>>> Enqueue(
        int id,
        PlaylistEnqueueRequest request,
        CancellationToken ct)
    {
        var result = await _imports.EnqueueAsync(id, request.ItemIds ?? Array.Empty<int>(), request.Selections, ct);
        return result is null
            ? NotFound(ApiResponse<PlaylistEnqueueResult>.Failure(StatusCodes.Status404NotFound, "No se encontró la importación de playlist."))
            : Ok(ApiResponse<PlaylistEnqueueResult>.Success(StatusCodes.Status200OK, "Tracks de playlist añadidos a la cola.", result));
    }
}

public sealed record PlaylistPreviewRequest(string Url);

public sealed record PlaylistEnqueueRequest(
    int[]? ItemIds,
    IReadOnlyDictionary<int, Guid>? Selections);
