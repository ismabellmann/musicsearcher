using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ProtectedSettingsService _settings;

    public SettingsController(ProtectedSettingsService settings) => _settings = settings;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<SettingsView>>> Get(CancellationToken ct)
    {
        var view = new SettingsView(
            await _settings.IsConfiguredAsync("ListenBrainzToken", ct),
            await _settings.IsConfiguredAsync("ApplicationSecret", ct));
        return Ok(ApiResponse<SettingsView>.Success(StatusCodes.Status200OK, "Configuración consultada.", view));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<SettingsView>>> Update(UpdateSettingsRequest request, CancellationToken ct)
    {
        if (request.ListenBrainzToken is not null)
            await _settings.SetSecretAsync("ListenBrainzToken", request.ListenBrainzToken, ct);
        return await Get(ct);
    }

    [HttpPost("secrets/generate")]
    public async Task<ActionResult<ApiResponse<SettingsView>>> Generate(GenerateSecretRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Name, "ApplicationSecret", StringComparison.Ordinal))
            return BadRequest(ApiResponse<SettingsView>.Failure(StatusCodes.Status400BadRequest, "El secreto solicitado no está permitido."));
        await _settings.SetSecretAsync(request.Name, ProtectedSettingsService.GenerateSecret(), ct);
        return await Get(ct);
    }
}

public sealed record SettingsView(bool ListenBrainzConfigured, bool ApplicationSecretConfigured);
public sealed record UpdateSettingsRequest(string? ListenBrainzToken);
public sealed record GenerateSecretRequest(string Name);
