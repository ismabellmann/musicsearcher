using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace MusicSync.Infrastructure;

public interface IJobEventPublisher
{
    Task PublishAsync(int jobId, string type, string status, string message, CancellationToken ct);
}

public sealed class JobEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<DownloadJobEvent>> _subscribers = new();

    public (Guid Id, ChannelReader<DownloadJobEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<DownloadJobEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    public void Publish(DownloadJobEvent @event)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(@event);
    }
}

public sealed class JobEventPublisher : IJobEventPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobEventHub _hub;

    public JobEventPublisher(IServiceScopeFactory scopeFactory, JobEventHub hub)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
    }

    public async Task PublishAsync(int jobId, string type, string status, string message, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var @event = new DownloadJobEvent
        {
            JobId = jobId,
            Type = type,
            Status = status,
            Message = message,
            CreatedAt = DateTime.UtcNow
        };
        db.DownloadJobEvents.Add(@event);
        await db.SaveChangesAsync(ct);
        _hub.Publish(@event);
    }
}
