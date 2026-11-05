using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicSync.Api.Controllers;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class DownloadCandidateSelectionTests : IDisposable
{
    private const string BestUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
    private const string LiveUrl = "https://www.youtube.com/watch?v=ccccccccccc";
    private const string WeakUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-candidates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Worker_StoresRankedCandidates_AndSelectsTheBest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new LibraryOptions { Root = Path.Combine(_root, "library"), StagingDirectory = Path.Combine(_root, "staging") });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddSingleton<ICoverArtProvider, NoCoverArtProvider>();
        services.AddSingleton<LibraryPathResolver>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        services.AddSingleton<IAcquisitionProvider, RankedCandidateProvider>();
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
            var job = new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Queued, ProviderName = "ranked" };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = serviceProvider.GetRequiredService<MusicSync.Worker.Worker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            DownloadJob? persisted = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                if (persisted.Status is not ("Queued" or "Resolving" or "Searching"))
                    break;
            }

            Assert.NotNull(persisted);
            Assert.Equal(DownloadJobStatus.NeedsReview, persisted.Status);
            Assert.Equal(BestUrl, persisted.SourceUrl);

            using var verifyScope = serviceProvider.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
            var stored = await verifyDb.DownloadJobCandidates.AsNoTracking()
                .Where(item => item.DownloadJobId == jobId)
                .OrderBy(item => item.Rank)
                .ToListAsync();
            Assert.Equal(new[] { BestUrl, LiveUrl, WeakUrl }, stored.Select(item => item.SourceUrl));
            Assert.Equal(new[] { 1, 2, 3 }, stored.Select(item => item.Rank));
            Assert.True(stored[0].Score > stored[1].Score);
            Assert.True(stored[1].Score > stored[2].Score);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Approve_WithCandidateFromSameJob_RecordsThatSource()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options;
        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var job = await SeedReviewJobAsync(db, "Song", new[] { (BestUrl, 0.7f), (LiveUrl, 0.65f) });
        var liveCandidate = await db.DownloadJobCandidates.AsNoTracking()
            .SingleAsync(item => item.DownloadJobId == job.Id && item.SourceUrl == LiveUrl);

        var controller = CreateController(db);
        var result = await controller.Approve(job.Id, new ApproveDownloadRequest(liveCandidate.Id), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal(DownloadJobStatus.Approved, persisted.Status);
        Assert.Equal(LiveUrl, persisted.SourceUrl);
        Assert.Equal(0.65f, persisted.MatchScore!.Value, 3);
    }

    [Fact]
    public async Task Approve_WithCandidateFromAnotherJob_IsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options;
        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var first = await SeedReviewJobAsync(db, "First", new[] { (BestUrl, 0.7f) });
        var second = await SeedReviewJobAsync(db, "Second", new[] { (WeakUrl, 0.2f) });
        var foreignCandidate = await db.DownloadJobCandidates.AsNoTracking()
            .SingleAsync(item => item.DownloadJobId == second.Id);

        var controller = CreateController(db);
        var result = await controller.Approve(first.Id, new ApproveDownloadRequest(foreignCandidate.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == first.Id);
        Assert.Equal(DownloadJobStatus.NeedsReview, persisted.Status);
        Assert.Equal(BestUrl, persisted.SourceUrl);
    }

    [Fact]
    public async Task Detail_ReturnsCandidatesInRankOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options;
        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var job = await SeedReviewJobAsync(db, "Song", new[] { (BestUrl, 0.7f), (LiveUrl, 0.65f), (WeakUrl, 0.1f) });

        var controller = CreateController(db);
        var result = await controller.Get(job.Id, CancellationToken.None);

        var body = Assert.IsType<OkObjectResult>(result.Result).Value as ApiResponse<DownloadJobSummary>;
        Assert.NotNull(body?.Data?.Candidates);
        Assert.Equal(new[] { 1, 2, 3 }, body!.Data!.Candidates!.Select(item => item.Rank));
        Assert.Equal(new[] { BestUrl, LiveUrl, WeakUrl }, body.Data.Candidates!.Select(item => item.SourceUrl));
    }

    private static DownloadsController CreateController(MusicSyncDbContext db) =>
        new(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });

    private static async Task<DownloadJob> SeedReviewJobAsync(
        MusicSyncDbContext db,
        string title,
        IEnumerable<(string Url, float Score)> candidates)
    {
        var track = new Track { RecordingMbid = Guid.NewGuid(), Title = title, ArtistName = "Artist", DurationMs = 180000 };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var list = candidates.ToList();
        var job = new DownloadJob
        {
            TrackId = track.Id,
            Status = DownloadJobStatus.NeedsReview,
            ProviderName = "deterministic",
            SourceUrl = list[0].Url,
            MatchScore = list[0].Score
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();

        for (var index = 0; index < list.Count; index++)
        {
            db.DownloadJobCandidates.Add(new DownloadJobCandidate
            {
                DownloadJobId = job.Id,
                Rank = index + 1,
                SourceUrl = list[index].Url,
                Title = title,
                DurationMs = 180000,
                Score = list[index].Score
            });
        }
        await db.SaveChangesAsync();
        return job;
    }

    private sealed class RankedCandidateProvider : IAcquisitionProvider
    {
        public string Name => "ranked";

        public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct)
        {
            IReadOnlyList<SourceCandidate> candidates = new[]
            {
                new SourceCandidate { Title = "Other thing", SourceUrl = WeakUrl, DurationMs = 1000 },
                new SourceCandidate { Title = "Song", SourceUrl = BestUrl, DurationMs = 180000 },
                new SourceCandidate { Title = "Song (Live)", SourceUrl = LiveUrl, DurationMs = 180000 }
            };
            return Task.FromResult(candidates);
        }

        public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct) =>
            throw new NotSupportedException("Candidate selection tests do not download.");
    }
}
