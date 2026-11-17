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
