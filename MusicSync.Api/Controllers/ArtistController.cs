using Microsoft.AspNetCore.Mvc;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArtistController : ControllerBase
{
    private readonly IMetadataProvider _metadataProvider;
    private readonly IPopularityProvider _popularityProvider;
    private readonly ILogger<ArtistController> _logger;

    public ArtistController(
        IMetadataProvider metadataProvider,
        IPopularityProvider popularityProvider,
        ILogger<ArtistController> logger)
    {
        _metadataProvider = metadataProvider;
        _popularityProvider = popularityProvider;
        _logger = logger;
    }

    [HttpGet("{mbid}/top")]
    [ProducesResponseType(typeof(IReadOnlyList<RankedRecording>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Top(Guid mbid, CancellationToken ct, int count = 10)
    {
        if (count <= 0 || count > 50)
        {
            return BadRequest("Count must be between 1 and 50");
        }

        _logger.LogInformation("Fetching top recordings for artist MBID: {Mbid}, count: {Count}", mbid, count);

        var recordings = await _popularityProvider.TopRecordingsForArtistAsync(mbid, count, ct);

        if (recordings.Count == 0)
        {
            return NotFound($"No top recordings found for artist MBID: {mbid}");
        }

        return Ok(recordings);
    }
}