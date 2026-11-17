using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MusicSync.Infrastructure;

/// <summary>
/// Manages the single local account. Passwords are stored only as salted PBKDF2 hashes
/// produced by ASP.NET's PasswordHasher; they cannot be recovered from the database.
/// </summary>
public sealed class AccountService
{
    public const string SecurityStampClaim = "musicsync.security_stamp";
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    private readonly MusicSyncDbContext _db;
    private readonly PasswordHasher<UserAccount> _hasher = new();

    public AccountService(MusicSyncDbContext db)
    {
        _db = db;
    }

    public Task<bool> IsSetupCompleteAsync(CancellationToken ct) =>
        _db.UserAccounts.AnyAsync(ct);

    /// <summary>
    /// Creates the first account. Returns null when setup has already been completed.
    /// </summary>
    public async Task<UserAccount?> CreateInitialAccountAsync(string username, string password, CancellationToken ct)
    {
        await SetupLock.WaitAsync(ct);
        try
        {
            if (await IsSetupCompleteAsync(ct))
                return null;

            var account = new UserAccount
            {
                Username = username.Trim(),
                SecurityStamp = CreateSecurityStamp()
            };
            account.PasswordHash = _hasher.HashPassword(account, password);
            _db.UserAccounts.Add(account);
            await _db.SaveChangesAsync(ct);
            return account;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    /// <summary>
    /// Returns the account when the credentials match, otherwise null.
    /// </summary>
    public async Task<UserAccount?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        var account = await _db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Username == username.Trim(), ct);

        if (account is null)
        {
            // Hash anyway so that unknown usernames take about as long as wrong passwords.
            _hasher.HashPassword(new UserAccount(), password);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : account;
    }

    public async Task<bool> ChangePasswordAsync(int accountId, string currentPassword, string newPassword, CancellationToken ct)
    {
        var account = await _db.UserAccounts.FirstOrDefaultAsync(item => item.Id == accountId, ct);
        if (account is null)
            return false;

        if (_hasher.VerifyHashedPassword(account, account.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return false;

        account.PasswordHash = _hasher.HashPassword(account, newPassword);
        account.SecurityStamp = CreateSecurityStamp();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeAllSessionsAsync(int accountId, CancellationToken ct)
    {
        var account = await _db.UserAccounts.FirstOrDefaultAsync(item => item.Id == accountId, ct);
        if (account is null)
            return false;

        account.SecurityStamp = CreateSecurityStamp();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string CreateSecurityStamp() => Guid.NewGuid().ToString("N");
}
