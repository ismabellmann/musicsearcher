using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/ratings")]
public sealed class RatingsController : ControllerBase
{
    private readonly RatingService _ratings;
    public RatingsController(RatingService ratings) => _ratings = ratings;

    [HttpGet("{targetType}/{*targetKey}")]
    public async Task<ActionResult<ApiResponse<RatingView>>> Get(string targetType, string targetKey, CancellationToken ct)
    {
        var rating = await _ratings.GetAsync(targetType, targetKey, ct);
        return Ok(ApiResponse<RatingView>.Success(StatusCodes.Status200OK, "Valoración consultada.", ToView(rating, targetType, targetKey)));
    }

    [HttpPut("{targetType}/{*targetKey}")]
    public async Task<ActionResult<ApiResponse<RatingView>>> Put(string targetType, string targetKey, RatingRequest request, CancellationToken ct)
    {
        try
        {
            var rating = await _ratings.SetAsync(targetType, targetKey, request.Value, request.Notes, ct);
            return Ok(ApiResponse<RatingView>.Success(StatusCodes.Status200OK, "Valoración guardada.", ToView(rating, targetType, targetKey)));
        }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<RatingView>.Failure(StatusCodes.Status400BadRequest, ex.Message)); }
    }

    [HttpDelete("{targetType}/{*targetKey}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(string targetType, string targetKey, CancellationToken ct)
    {
        await _ratings.DeleteAsync(targetType, targetKey, ct);
        return Ok(ApiResponse<object>.Success(StatusCodes.Status200OK, "Valoración eliminada.", new object()));
    }

    private static RatingView ToView(Rating? rating, string type, string key) =>
        new(type, key, rating?.Value ?? 0, rating?.Notes, rating?.UpdatedAt);
}

public sealed record RatingRequest(int Value, string? Notes);
public sealed record RatingView(string TargetType, string TargetKey, int Value, string? Notes, DateTime? UpdatedAt);
