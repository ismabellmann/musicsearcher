using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/library/albums")]
public sealed class AlbumsController : ControllerBase
{
    private readonly AlbumLibraryService _albums;
    public AlbumsController(AlbumLibraryService albums) => _albums = albums;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AlbumLibraryView>>>> List(CancellationToken ct)
    {
        var albums = await _albums.ListAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<AlbumLibraryView>>.Success(200, "Álbumes consultados.", albums));
    }

    [HttpGet("{*key}")]
    public async Task<ActionResult<ApiResponse<AlbumLibraryDetail>>> Get(string key, CancellationToken ct)
    {
        var album = await _albums.GetAsync(key, ct);
        return album is null
            ? NotFound(ApiResponse<AlbumLibraryDetail>.Failure(404, "No se encontró el álbum."))
            : Ok(ApiResponse<AlbumLibraryDetail>.Success(200, "Álbum consultado.", album));
    }
}
