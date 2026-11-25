using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/library/metadata")]
public sealed class LibraryMetadataController : ControllerBase
{
    private readonly LibraryMetadataService _metadata;
    public LibraryMetadataController(LibraryMetadataService metadata) => _metadata = metadata;

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<MetadataRefreshSummary>>> Refresh(MetadataRefreshRequest? request, CancellationToken ct)
    {
        var summary = await _metadata.RefreshAsync(request?.FileIds, ct);
        return Ok(ApiResponse<MetadataRefreshSummary>.Success(200, "Metadatos actualizados.", summary));
    }
}

public sealed record MetadataRefreshRequest(int[]? FileIds);
