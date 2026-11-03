using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicSync.Api.Controllers;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class DownloadJobPersistenceTests
{
    [Fact]
    public async Task TrackAndDownloadJob_PersistToSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var track = new Track
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Persisted Song",
            ArtistName = "Persisted Artist",
            DurationMs = 180000
        };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        db.DownloadJobs.Add(new DownloadJob
        {
            TrackId = track.Id,
            Status = "Queued",
            ProviderName = "deterministic"
        });
        await db.SaveChangesAsync();

        var persisted = await db.DownloadJobs.SingleAsync();
        Assert.Equal(track.Id, persisted.TrackId);
        Assert.Equal("Queued", persisted.Status);
        Assert.Equal("deterministic", persisted.ProviderName);
    }

    [Fact]
    public async Task MigrateAsync_CreatesNewDatabaseSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await MusicSyncDatabaseMigrator.MigrateAsync(db);

        Assert.True(await db.Database.CanConnectAsync());
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task MigrateAsync_BaselinesEnsureCreatedDatabaseWithoutLosingData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261029222411_InitialCreate");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
        var track = new Track
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Legacy Track",
            ArtistName = "Legacy Artist",
            DurationMs = 120000
        };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        await MusicSyncDatabaseMigrator.MigrateAsync(db);

        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        var persistedTrack = await db.Tracks.SingleAsync();
        Assert.Equal(track.RecordingMbid, persistedTrack.RecordingMbid);
        Assert.Equal("Legacy Track", persistedTrack.Title);
    }

    [Fact]
    public async Task MigrateAsync_RejectsUnknownLegacySchemaWithoutChangingIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE Tracks (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MusicSyncDbContext(options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MusicSyncDatabaseMigrator.MigrateAsync(db));

        Assert.Contains("database was left unchanged", error.Message);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Tracks');";
        Assert.Equal(1L, (long)(await inspect.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CreateDownloadRequest_ReturnsFeedbackAndPersistsTrackAndJob()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recordingMbid = Guid.NewGuid();
        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var result = await controller.Create(
            new CreateDownloadRequest(new TrackInfo
            {
                RecordingMbid = recordingMbid,
                Title = "Queued Song",
                ArtistName = "Queued Artist",
                DurationMs = 180000
            }),
            CancellationToken.None);

        var response = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ObjectResult>(result.Result);
        Assert.Equal(202, response.StatusCode);

        var body = Assert.IsType<ApiResponse<DownloadJobCreated>>(response.Value);
        Assert.Equal(202, body.Status);
        Assert.Equal("Canción añadida a la cola exitosamente.", body.Msg);
        Assert.NotNull(body.Data);
        Assert.Equal("Queued", body.Data.JobStatus);

        var persistedTrack = await db.Tracks.SingleAsync();
        var persistedJob = await db.DownloadJobs.SingleAsync();
        Assert.Equal(recordingMbid, persistedTrack.RecordingMbid);
        Assert.Equal(persistedTrack.Id, persistedJob.TrackId);
        Assert.Equal(body.Data.JobId, persistedJob.Id);
    }

    [Fact]
    public async Task ListQueuedJobs_ReturnsFifoOrderAndQueuePositions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var track = await CreateTrackAsync(db, "Queue Track");
        var first = await CreateJobAsync(db, track.Id, DownloadJobStatus.Queued);
        var second = await CreateJobAsync(db, track.Id, DownloadJobStatus.Queued);
        await CreateJobAsync(db, track.Id, DownloadJobStatus.Failed);

        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var result = await controller.List(
            DownloadJobStatus.Queued,
            page: 1,
            pageSize: 10,
            CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<PagedResult<DownloadJobSummary>>>(response.Value);
        Assert.Equal(200, body.Status);
        Assert.Equal(2, body.Data!.TotalCount);
        Assert.Collection(
            body.Data.Items,
            item =>
            {
                Assert.Equal(first.Id, item.JobId);
                Assert.Equal(1, item.QueuePosition);
                Assert.Equal("Queue Track", item.TrackTitle);
            },
            item =>
            {
                Assert.Equal(second.Id, item.JobId);
                Assert.Equal(2, item.QueuePosition);
            });
    }

    [Fact]
    public async Task GetJob_ReturnsNotFoundForUnknownId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });

        var result = await controller.Get(404, CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<DownloadJobSummary>>(response.Value);
        Assert.Equal(404, body.Status);
    }

    [Fact]
    public async Task ListJobs_RejectsUnknownStatusAndInvalidPagination()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });

        var invalidStatus = await controller.List("Unknown", 1, 50, CancellationToken.None);
        var statusResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(invalidStatus.Result);
        Assert.Equal(400, Assert.IsType<ApiResponse<PagedResult<DownloadJobSummary>>>(statusResponse.Value).Status);

        var invalidPage = await controller.List(null, int.MaxValue, 100, CancellationToken.None);
        var pageResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(invalidPage.Result);
        Assert.Equal(400, Assert.IsType<ApiResponse<PagedResult<DownloadJobSummary>>>(pageResponse.Value).Status);
    }

    [Fact]
    public async Task CancelJob_CancelsQueuedJobAndRejectsActiveJob()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var track = await CreateTrackAsync(db, "Cancel Track");
        var queued = await CreateJobAsync(db, track.Id, DownloadJobStatus.Queued);
        var active = await CreateJobAsync(db, track.Id, DownloadJobStatus.Searching);

        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var cancelledResult = await controller.Cancel(queued.Id, CancellationToken.None);
        var cancelledResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(cancelledResult.Result);
        var cancelledBody = Assert.IsType<ApiResponse<DownloadJobSummary>>(cancelledResponse.Value);
        Assert.Equal(DownloadJobStatus.Cancelled, cancelledBody.Data!.JobStatus);
        Assert.NotNull(cancelledBody.Data.FinishedAt);

        var activeResult = await controller.Cancel(active.Id, CancellationToken.None);
        var activeResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(activeResult.Result);
        var activeBody = Assert.IsType<ApiResponse<DownloadJobSummary>>(activeResponse.Value);
        Assert.Equal(409, activeBody.Status);
        Assert.Contains(DownloadJobStatus.Searching, activeBody.Msg);
    }

    [Fact]
    public async Task ApproveJob_PersistsApprovalWithoutStartingDownload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var track = await CreateTrackAsync(db, "Review Track");
        var reviewJob = await CreateJobAsync(db, track.Id, DownloadJobStatus.NeedsReview);
        reviewJob.SourceUrl = "https://example.test/candidate";
        reviewJob.MatchScore = 0.65f;
        reviewJob.Error = "Score 0.65 requires review";
        reviewJob.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var result = await controller.Approve(reviewJob.Id, null, CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<DownloadJobSummary>>(response.Value);
        Assert.Equal(200, body.Status);
        Assert.Contains("La descarga se iniciará", body.Msg);
        Assert.Equal(DownloadJobStatus.Approved, body.Data!.JobStatus);
        Assert.Equal("https://example.test/candidate", body.Data.SourceUrl);
        Assert.Equal(0.65f, body.Data.MatchScore);
        Assert.Null(body.Data.Error);
        Assert.Null(body.Data.FinishedAt);

        var persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(job => job.Id == reviewJob.Id);
        Assert.Equal(DownloadJobStatus.Approved, persisted.Status);
        Assert.Equal("https://example.test/candidate", persisted.SourceUrl);
        Assert.Equal(0.65f, persisted.MatchScore);
    }

    [Fact]
    public async Task ApproveJob_RejectsJobsNotAwaitingReview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var track = await CreateTrackAsync(db, "Already Queued Track");
        var queuedJob = await CreateJobAsync(db, track.Id, DownloadJobStatus.Queued);

        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var result = await controller.Approve(queuedJob.Id, null, CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<DownloadJobSummary>>(response.Value);
        Assert.Equal(409, body.Status);
        Assert.Contains(DownloadJobStatus.Queued, body.Msg);
        Assert.Equal(DownloadJobStatus.Queued, (await db.DownloadJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ApproveJob_ReturnsNotFoundForUnknownId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });

        var result = await controller.Approve(404, null, CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result.Result);
        Assert.Equal(404, Assert.IsType<ApiResponse<DownloadJobSummary>>(response.Value).Status);
    }

    [Fact]
    public async Task RetryJob_RequeuesFailedJobAndPreservesAttemptCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var track = await CreateTrackAsync(db, "Retry Track");
        var failed = await CreateJobAsync(db, track.Id, DownloadJobStatus.Failed);
        failed.Attempts = 2;
        failed.Error = "Transient failure";
        failed.SourceUrl = "old-source";
        failed.MatchScore = 0.6f;
        failed.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var controller = new DownloadsController(db, new IAcquisitionProvider[] { new DeterministicAcquisitionProvider() });
        var result = await controller.Retry(failed.Id, CancellationToken.None);

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(result.Result);
        var body = Assert.IsType<ApiResponse<DownloadJobSummary>>(response.Value);
        Assert.Equal(202, body.Status);
        Assert.Equal(DownloadJobStatus.Queued, body.Data!.JobStatus);
        Assert.Equal(1, body.Data.QueuePosition);
        Assert.Equal(2, body.Data.Attempts);
        Assert.Null(body.Data.Error);
        Assert.Null(body.Data.SourceUrl);
        Assert.Null(body.Data.MatchScore);
        Assert.Null(body.Data.FinishedAt);
    }

    [Fact]
    public async Task Worker_ProcessesPersistedJobAndSavesBestCandidateBeforeTagging()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IAcquisitionProvider, DeterministicAcquisitionProvider>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "musicsync-worker-test-" + Guid.NewGuid().ToString("N"));
        services.AddSingleton(new LibraryOptions { Root = Path.Combine(stagingDirectory, "library"), StagingDirectory = stagingDirectory });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<LibraryPathResolver>();
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
                Title = "Matched Song",
                ArtistName = "Matched Artist",
                DurationMs = 180000
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();

            var job = new DownloadJob
            {
                TrackId = track.Id,
                Status = "Queued",
                ProviderName = "deterministic"
            };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = serviceProvider.GetRequiredService<MusicSync.Worker.Worker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            DownloadJob? persistedJob = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                persistedJob = await db.DownloadJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
                if (persistedJob.Status != "Queued"
                    && persistedJob.Status != "Resolving"
                    && persistedJob.Status != "Searching"
                    && persistedJob.Status != "ReadyForTagging"
                    && persistedJob.Status != "Downloading")
                {
                    break;
                }

            }

            Assert.NotNull(persistedJob);
            Assert.Equal("NeedsReview", persistedJob.Status);
            Assert.Equal("deterministic", persistedJob.ProviderName);
            Assert.Equal($"deterministic://{(await ReadRecordingMbidAsync(serviceProvider, jobId))}", persistedJob.SourceUrl);
            Assert.Equal(0.7f, persistedJob.MatchScore!.Value, 3);
            Assert.Contains("requires review", persistedJob.Error);
            Assert.NotNull(persistedJob.FinishedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> ReadRecordingMbidAsync(IServiceProvider serviceProvider, int jobId)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
        var job = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        return await db.Tracks
            .Where(track => track.Id == job.TrackId)
            .Select(track => track.RecordingMbid)
            .SingleAsync();
    }

    private static async Task<Track> CreateTrackAsync(MusicSyncDbContext db, string title)
    {
        var track = new Track
        {
            RecordingMbid = Guid.NewGuid(),
            Title = title,
            ArtistName = "Test Artist",
            DurationMs = 180000
        };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private static async Task<DownloadJob> CreateJobAsync(
        MusicSyncDbContext db,
        int trackId,
        string status)
    {
        var job = new DownloadJob { TrackId = trackId, Status = status };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }
}
