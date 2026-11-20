using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/youtube")]
public sealed class YouTubeController : ControllerBase
{
    private readonly YouTubeLinkService _youtube;
    public YouTubeController(YouTubeLinkService youtube) => _youtube = youtube;

    [HttpPost("preview")]
    public async Task<ActionResult<ApiResponse<YouTubePreview>>> Preview(YouTubePreviewRequest request, CancellationToken ct)
    {
        try { return Ok(ApiResponse<YouTubePreview>.Success(200, "Video de YouTube consultado.", await _youtube.PreviewAsync(request.Url, ct))); }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<YouTubePreview>.Failure(400, ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<YouTubePreview>.Failure(400, ex.Message)); }
    }

    [HttpPost("enqueue")]
    public async Task<ActionResult<ApiResponse<YouTubeEnqueueResult>>> Enqueue(YouTubeEnqueueRequest request, CancellationToken ct)
    {
        try { return Accepted(ApiResponse<YouTubeEnqueueResult>.Success(202, "Video de YouTube añadido a la cola.", await _youtube.EnqueueAsync(request.Url, request.RecordingMbid, ct))); }
        catch (InvalidOperationException ex) { return Conflict(ApiResponse<YouTubeEnqueueResult>.Failure(409, ex.Message)); }
    }
}

public sealed record YouTubePreviewRequest(string Url);
public sealed record YouTubeEnqueueRequest(string Url, Guid RecordingMbid);
