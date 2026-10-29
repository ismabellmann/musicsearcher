using Microsoft.AspNetCore.Mvc;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly IMetadataProvider _metadataProvider;
    private readonly IPopularityProvider _popularityProvider;
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        IMetadataProvider metadataProvider,
        IPopularityProvider popularityProvider,
        ILogger<SearchController> logger)
    {
        _metadataProvider = metadataProvider;
        _popularityProvider = popularityProvider;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrackInfo>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Get(string type, string q, string? sort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Query parameter 'q' is required");
        }

        _logger.LogInformation("Search request: type={Type}, query={Query}, sort={Sort}", type, q, sort);

        IReadOnlyList<TrackInfo> results = type.ToLower() switch
        {
            "track" => await _metadataProvider.SearchTracksAsync(q, ct),
            "album" => await _metadataProvider.SearchAlbumsAsync(q, ct),
            "artist" => await _metadataProvider.SearchArtistsAsync(q, ct),
            _ => System.Array.Empty<TrackInfo>()
        };

        if (results.Count > 0 && _popularityProvider != null)
        {
            _logger.LogInformation("Enriching {Count} search results with popularity data", results.Count);
        }

        return Ok(results);
    }
}