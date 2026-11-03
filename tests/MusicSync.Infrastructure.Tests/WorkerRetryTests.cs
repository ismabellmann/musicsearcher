using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class WorkerRetryTests : IDisposable
{
    private const string Url = "https://www.youtube.com/watch?v=aaaaaaaaaaa";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-retry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_AndThenSucceeds()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? throw new TransientAcquisitionException("HTTP Error 503")
            : OneCandidate());

        var job = await RunAsync(provider, nextAttemptAt: null, untilDone: j => j.Status == DownloadJobStatus.NeedsReview);

        Assert.Equal(DownloadJobStatus.NeedsReview, job.Status);
        Assert.Equal(1, job.AutomaticRetries);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(2, provider.Calls);
        Assert.Null(job.NextAttemptAt);
    }

    [Fact]
    public async Task PermanentFailure_FailsImmediatelyWithoutRetry()
    {
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("bad request"));

        var job = await RunAsync(provider, nextAttemptAt: null, untilDone: j => j.Status == DownloadJobStatus.Failed);

        Assert.Equal(DownloadJobStatus.Failed, job.Status);
        Assert.Equal(0, job.AutomaticRetries);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("bad request", job.Error);
    }

    [Fact]
    public async Task TransientFailure_ExhaustsRetryBudget_ThenFailsWithAttemptCount()
    {
        var provider = new ScriptedProvider(_ => throw new TransientAcquisitionException("HTTP Error 429"));

        var job = await RunAsync(provider, nextAttemptAt: null, untilDone: j => j.Status == DownloadJobStatus.Failed);

        Assert.Equal(DownloadJobStatus.Failed, job.Status);
        Assert.Equal(2, job.AutomaticRetries);
        Assert.Equal(3, job.Attempts);
        Assert.Equal(3, provider.Calls);
        Assert.Contains("Failed after 3 attempts", job.Error);
    }

    [Fact]
    public async Task JobWithFutureNextAttemptAt_IsNotClaimed()
    {
        var provider = new ScriptedProvider(_ => OneCandidate());

        var job = await RunAsync(
            provider,
            nextAttemptAt: DateTime.UtcNow.AddHours(1),
            untilDone: _ => false,
            waitFor: TimeSpan.FromSeconds(2));

        Assert.Equal(DownloadJobStatus.Queued, job.Status);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, job.Attempts);
    }

    private static IReadOnlyList<SourceCandidate> OneCandidate() => new[]
    {
        new SourceCandidate { Title = "Song", SourceUrl = Url, DurationMs = 180000 }
    };

    private async Task<DownloadJob> RunAsync(
        ScriptedProvider provider,
        DateTime? nextAttemptAt,
        Func<DownloadJob, bool> untilDone,
        TimeSpan? waitFor = null)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new LibraryOptions { Root = Path.Combine(_root, "library"), StagingDirectory = Path.Combine(_root, "staging") });
        services.AddSingleton(new WorkerOptions { MaxAutomaticRetries = 2, RetryBaseDelaySeconds = 0 });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<LibraryPathResolver>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        services.AddSingleton<IAcquisitionProvider>(provider);
        services.AddSingleton<MusicSync.Worker.Worker>();

        await using var serviceProvider = services.BuildServiceProvider();
        int jobId;
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
            await db.Database.EnsureCreatedAsync();
            var track = new Track { RecordingMbid = Guid.NewGuid(), Title = "Song", ArtistName = "Artist", DurationMs = 180000 };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();
            var job = new DownloadJob
            {
                TrackId = track.Id,
                Status = DownloadJobStatus.Queued,
                ProviderName = provider.Name,
                NextAttemptAt = nextAttemptAt
            };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = serviceProvider.GetRequiredService<MusicSync.Worker.Worker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + (waitFor ?? TimeSpan.FromSeconds(20));
            DownloadJob? persisted = null;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                if (untilDone(persisted))
                    break;
            }

            return persisted!;
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ScriptedProvider : IAcquisitionProvider
    {
        private readonly Func<int, IReadOnlyList<SourceCandidate>> _find;
        private int _calls;

        public ScriptedProvider(Func<int, IReadOnlyList<SourceCandidate>> find)
        {
            _find = find;
        }

        public string Name => "scripted";

        public int Calls => _calls;

        public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct)
        {
            _calls++;
            return Task.FromResult(_find(_calls));
        }

        public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct) =>
            throw new NotSupportedException("Retry tests do not download.");
    }
}
