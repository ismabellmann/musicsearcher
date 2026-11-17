using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSync.Infrastructure;

namespace MusicSync.Api.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController : ControllerBase
{
    private readonly MusicSyncDbContext _db;
    private readonly JobEventHub _hub;

    public EventsController(MusicSyncDbContext db, JobEventHub hub)
    {
        _db = db;
        _hub = hub;
    }

    [HttpGet]
    public async Task Stream(int? jobId, long? after, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var subscription = _hub.Subscribe();
        try
        {
            var cursor = after ?? ParseLastEventId();
            var recent = await _db.DownloadJobEvents.AsNoTracking()
                .Where(item => item.Id > cursor && (jobId == null || item.JobId == jobId))
                .OrderBy(item => item.Id)
                .Take(100)
                .ToListAsync(ct);

            foreach (var @event in recent)
            {
                await WriteEventAsync(@event, ct);
                cursor = @event.Id;
            }

            await Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var readTask = subscription.Reader.WaitToReadAsync(ct).AsTask();
                var keepAliveTask = Task.Delay(TimeSpan.FromSeconds(20), ct);
                var completed = await Task.WhenAny(readTask, keepAliveTask);
                if (completed == keepAliveTask)
                {
                    await Response.WriteAsync(": keep-alive\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }

                if (!await readTask)
                    break;
                while (subscription.Reader.TryRead(out var @event))
                {
                    if (@event.Id <= cursor || (jobId is not null && @event.JobId != jobId))
                        continue;
                    await WriteEventAsync(@event, ct);
                    cursor = @event.Id;
                }
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected.
        }
        finally
        {
            _hub.Unsubscribe(subscription.Id);
        }
    }

    private long ParseLastEventId()
    {
        return Request.Headers.TryGetValue("Last-Event-ID", out var value)
            && long.TryParse(value.ToString(), out var id)
                ? id
                : 0;
    }

    private async Task WriteEventAsync(DownloadJobEvent @event, CancellationToken ct)
    {
        await Response.WriteAsync($"id: {@event.Id}\n", ct);
        await Response.WriteAsync($"event: {@event.Type}\n", ct);
        await Response.WriteAsync(
            $"data: {JsonSerializer.Serialize(new JobEventResponse(@event.Id, @event.JobId, @event.Status, @event.Message, @event.CreatedAt))}\n\n",
            ct);
    }
}

public sealed record JobEventResponse(
    long EventId,
    int JobId,
    string Status,
    string Message,
    DateTime CreatedAt);
