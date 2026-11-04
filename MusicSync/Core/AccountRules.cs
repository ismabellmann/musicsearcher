using System.Text.RegularExpressions;

namespace MusicSync.Core;

/// <summary>
/// Credential rules for the single local account (section 11).
/// </summary>
public static class AccountRules
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._-]{3,50}$", RegexOptions.CultureInvariant);

    public static string? ValidateUsername(string? username) =>
        username is not null && UsernamePattern.IsMatch(username.Trim())
            ? null
            : "El usuario debe tener entre 3 y 50 caracteres: letras, números, punto, guion o guion bajo.";

    public static string? ValidatePassword(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinPasswordLength && password.Length <= MaxPasswordLength
            ? null
            : $"La contraseña debe tener entre {MinPasswordLength} y {MaxPasswordLength} caracteres.";
}
