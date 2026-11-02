using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
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
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TrackInfo>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TrackInfo>>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TrackInfo>>>> Get(
        string type,
        string q,
        string? sort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(ApiResponse<IReadOnlyList<TrackInfo>>.Failure(
                StatusCodes.Status400BadRequest,
                "El parámetro de búsqueda 'q' es obligatorio."));
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

        return Ok(ApiResponse<IReadOnlyList<TrackInfo>>.Success(
            StatusCodes.Status200OK,
            "Búsqueda completada exitosamente.",
            results));
    }
}