using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class WorkerCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-cancel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task ActiveDownloadCancellation_MarksJobCancelledAndCleansProviderWork()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(libraryRoot);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var provider = new BlockingProvider();
        var registry = new JobCancellationRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new LibraryOptions { Root = libraryRoot, StagingDirectory = staging });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddSingleton<ICoverArtProvider, NoCoverArtProvider>();
        services.AddSingleton<LibraryPathResolver>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        services.AddSingleton<JobCancellationRegistry>(registry);
        services.AddSingleton<IAcquisitionProvider>(provider);
        services.AddSingleton<MusicSync.Worker.Worker>();

        await using var serviceProvider = services.BuildServiceProvider();
        int jobId;
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
            await db.Database.EnsureCreatedAsync();
            var track = new Track
            {
                RecordingMbid = Guid.NewGuid(),
                Title = "Cancellation Song",
                ArtistName = "Cancellation Artist",
                DurationMs = 1000
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();
            var job = new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Queued, ProviderName = provider.Name };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = serviceProvider.GetRequiredService<MusicSync.Worker.Worker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(registry.Cancel(jobId));

            DownloadJob? job = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                job = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                if (job.Status == DownloadJobStatus.Cancelled)
                    break;
            }

            Assert.NotNull(job);
            Assert.Equal(DownloadJobStatus.Cancelled, job.Status);
            Assert.False(Directory.Exists(Path.Combine(staging, "downloads")));
            Assert.Equal(0, await ReadLibraryFileCountAsync(serviceProvider));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<int> ReadLibraryFileCountAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>().LibraryFiles.CountAsync();
    }

    private sealed class BlockingProvider : IAcquisitionProvider
    {
        public string Name => "blocking";
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SourceCandidate>>(new[]
            {
                new SourceCandidate
                {
                    Title = track.Title,
                    ArtistName = track.ArtistName,
                    DurationMs = track.DurationMs,
                    SourceUrl = "blocking://source"
                }
            });

        public async Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
        {
            DownloadStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }
}
