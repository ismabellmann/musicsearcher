using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class JobEventPublisherTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(_connection));
        collection.AddSingleton<JobEventHub>();
        collection.AddSingleton<IJobEventPublisher, JobEventPublisher>();
        _services = collection.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Publish_PersistsAndBroadcastsEvent()
    {
        var hub = _services.GetRequiredService<JobEventHub>();
        var subscription = hub.Subscribe();
        var publisher = _services.GetRequiredService<IJobEventPublisher>();

        await publisher.PublishAsync(7, "job.created", "Queued", "Created", CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(await subscription.Reader.WaitToReadAsync(timeout.Token));
        var broadcast = await subscription.Reader.ReadAsync(timeout.Token);
        Assert.Equal(7, broadcast.JobId);
        Assert.Equal("job.created", broadcast.Type);
        await using var scope = _services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>().DownloadJobEvents.SingleAsync();
        Assert.Equal(broadcast.Id, stored.Id);
        hub.Unsubscribe(subscription.Id);
    }
}
