using Microsoft.AspNetCore.Mvc;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlaylistController : ControllerBase
{
    private readonly IPlaylistSource _playlistSource;
    private readonly ILogger<PlaylistController> _logger;

    public PlaylistController(
        IPlaylistSource playlistSource,
        ILogger<PlaylistController> logger)
    {
        _playlistSource = playlistSource;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PlaylistInfo), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Get(string urlOrId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
        {
            return BadRequest("Playlist URL or ID is required");
        }

        _logger.LogInformation("Fetching Spotify playlist: {UrlOrId}", urlOrId);

        try
        {
            var playlist = await _playlistSource.GetPlaylistAsync(urlOrId, ct);
            return Ok(playlist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching playlist: {UrlOrId}", urlOrId);
            return StatusCode(500, new { error = "An unexpected error occurred" });
        }
    }
}