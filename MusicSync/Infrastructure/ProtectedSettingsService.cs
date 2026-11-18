using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MusicSync.Infrastructure;

public sealed class ProtectedSettingsService
{
    private const string SecretPrefix = "secret:";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;

    public ProtectedSettingsService(IServiceScopeFactory scopeFactory, IDataProtectionProvider provider)
    {
        _scopeFactory = scopeFactory;
        _protector = provider.CreateProtector("MusicSync.Settings.Secrets.v1");
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var value = await db.Settings.AsNoTracking()
            .Where(item => item.Key == SecretPrefix + name)
            .Select(item => item.Value)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return null; }
    }

    public async Task SetSecretAsync(string name, string? value, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var key = SecretPrefix + name;
        var setting = await db.Settings.SingleOrDefaultAsync(item => item.Key == key, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (setting is not null)
                db.Settings.Remove(setting);
        }
        else
        {
            setting ??= new Setting { Key = key };
            setting.Value = _protector.Protect(value.Trim());
            if (setting.EntityStateIsDetached(db))
                db.Settings.Add(setting);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsConfiguredAsync(string name, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(await GetSecretAsync(name, ct));

    public static string GenerateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

internal static class SettingStateExtensions
{
    public static bool EntityStateIsDetached(this Setting setting, MusicSyncDbContext db) =>
        db.Entry(setting).State == EntityState.Detached;
}
