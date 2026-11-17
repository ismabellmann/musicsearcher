using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private const string InvalidCredentials = "Usuario o contraseña incorrectos.";

    private readonly AccountService _accounts;

    public AuthController(AccountService accounts)
    {
        _accounts = accounts;
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> Status(CancellationToken ct)
    {
        var setupComplete = await _accounts.IsSetupCompleteAsync(ct);
        var signedIn = User.Identity?.IsAuthenticated ?? false;

        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK,
            "Estado de autenticación consultado.",
            new AuthStatus(setupComplete, signedIn, signedIn ? User.Identity?.Name : null)));
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> Setup(CredentialsRequest request, CancellationToken ct)
    {
        var error = AccountRules.ValidateUsername(request.Username) ?? AccountRules.ValidatePassword(request.Password);
        if (error is not null)
            return BadRequest(ApiResponse<AuthStatus>.Failure(StatusCodes.Status400BadRequest, error));

        var account = await _accounts.CreateInitialAccountAsync(request.Username!, request.Password!, ct);
        if (account is null)
        {
            return Conflict(ApiResponse<AuthStatus>.Failure(
                StatusCodes.Status409Conflict,
                "La configuración inicial ya fue completada."));
        }

        await SignInAsync(account);
        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK,
            "Cuenta creada. Sesión iniciada.",
            new AuthStatus(true, true, account.Username)));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> Login(CredentialsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized(ApiResponse<AuthStatus>.Failure(StatusCodes.Status401Unauthorized, InvalidCredentials));
        }

        var account = await _accounts.VerifyAsync(request.Username, request.Password, ct);
        if (account is null)
        {
            return Unauthorized(ApiResponse<AuthStatus>.Failure(StatusCodes.Status401Unauthorized, InvalidCredentials));
        }

        await SignInAsync(account);
        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK,
            "Sesión iniciada.",
            new AuthStatus(true, true, account.Username)));
    }

    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK,
            "Sesión cerrada.",
            new AuthStatus(true, false, null)));
    }

    [HttpPost("password")]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var error = AccountRules.ValidatePassword(request.NewPassword);
        if (error is not null)
            return BadRequest(ApiResponse<AuthStatus>.Failure(StatusCodes.Status400BadRequest, error));

        var accountId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;

        var changed = await _accounts.ChangePasswordAsync(accountId, request.CurrentPassword ?? string.Empty, request.NewPassword!, ct);
        if (!changed)
        {
            return BadRequest(ApiResponse<AuthStatus>.Failure(
                StatusCodes.Status400BadRequest,
                "La contraseña actual no es correcta."));
        }

        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK,
            "Contraseña actualizada.",
            new AuthStatus(true, true, User.Identity?.Name)));
    }

    [HttpPost("logout-all")]
    public async Task<ActionResult<ApiResponse<AuthStatus>>> LogoutAll(CancellationToken ct)
    {
        var accountId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
        if (!await _accounts.RevokeAllSessionsAsync(accountId, ct))
            return Unauthorized(ApiResponse<AuthStatus>.Failure(StatusCodes.Status401Unauthorized, InvalidCredentials));

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse<AuthStatus>.Success(
            StatusCodes.Status200OK, "Todas las sesiones fueron revocadas.", new AuthStatus(true, false, null)));
    }

    private async Task SignInAsync(UserAccount account)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(AccountService.SecurityStampClaim, account.SecurityStamp)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }
}

public sealed record CredentialsRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AuthStatus(bool SetupComplete, bool SignedIn, string? Username);
