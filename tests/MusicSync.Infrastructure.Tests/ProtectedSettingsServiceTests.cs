using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class ProtectedSettingsServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
            Path.Combine(Path.GetTempPath(), "musicsync-test-keys-" + Guid.NewGuid().ToString("N"))));
        services.AddSingleton<ProtectedSettingsService>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Secret_IsEncryptedAtRestAndReadableThroughService()
    {
        var service = _services.GetRequiredService<ProtectedSettingsService>();
        const string secret = "listenbrainz-token-secret";

        await service.SetSecretAsync("ListenBrainzToken", secret, CancellationToken.None);

        Assert.Equal(secret, await service.GetSecretAsync("ListenBrainzToken", CancellationToken.None));
        await using var scope = _services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>().Settings.SingleAsync();
        Assert.DoesNotContain(secret, stored.Value);
        Assert.NotEqual(secret, stored.Value);
    }

    [Fact]
    public async Task EmptySecret_RemovesStoredValue()
    {
        var service = _services.GetRequiredService<ProtectedSettingsService>();
        await service.SetSecretAsync("ApplicationSecret", "value", CancellationToken.None);
        await service.SetSecretAsync("ApplicationSecret", "", CancellationToken.None);

        Assert.False(await service.IsConfiguredAsync("ApplicationSecret", CancellationToken.None));
    }
}
