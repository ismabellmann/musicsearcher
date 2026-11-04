using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSync.Api.Models;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get()
    {
        return Ok(ApiResponse<object>.Success(
            StatusCodes.Status200OK,
            "Servicio disponible.",
            new { status = "ok" }));
    }
}
