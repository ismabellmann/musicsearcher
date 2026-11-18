using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics) => _analytics = analytics;

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<AnalyticsSummary>>> Summary(
        int topArtistLimit = 10,
        CancellationToken ct = default)
    {
        if (topArtistLimit is < 1 or > 50)
            return BadRequest(ApiResponse<AnalyticsSummary>.Failure(StatusCodes.Status400BadRequest, "topArtistLimit debe estar entre 1 y 50."));

        var summary = await _analytics.GetSummaryAsync(topArtistLimit, ct);
        return Ok(ApiResponse<AnalyticsSummary>.Success(StatusCodes.Status200OK, "Resumen consultado.", summary));
    }
}
