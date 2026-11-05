using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class WorkerDuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-duplicate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task QueuedJob_StoredRecording_IsMarkedAlreadyInLibraryWithoutCallingProvider()
        => await RunScenarioAsync(DownloadJobStatus.Queued, seedLibraryFile: true, seedTargetFile: true, "AlreadyInLibrary");

    [Fact]
    public async Task QueuedJob_ExistingTargetFile_IsMarkedAlreadyExistsWithoutCallingProvider()
        => await RunScenarioAsync(DownloadJobStatus.Queued, seedLibraryFile: false, seedTargetFile: true, "AlreadyExists");

    [Fact]
    public async Task ApprovedJob_StoredRecording_IsMarkedAlreadyInLibraryWithoutCallingProvider()
        => await RunScenarioAsync(DownloadJobStatus.Approved, seedLibraryFile: true, seedTargetFile: true, "AlreadyInLibrary");

    private async Task RunScenarioAsync(
        string jobStatus,
        bool seedLibraryFile,
        bool seedTargetFile,
        string expectedNote)
    {
        var libraryRoot = Path.Combine(_root, "library");
        var stagingDirectory = Path.Combine(_root, "staging");
        var target = Path.Combine(libraryRoot, "Test Artist", "Stored Song.mp3");

        if (seedTargetFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "existing audio");
        }

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new LibraryOptions { Root = libraryRoot, StagingDirectory = stagingDirectory });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddSingleton<ICoverArtProvider, NoCoverArtProvider>();
        services.AddSingleton<LibraryPathResolver>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        var provider = new MustNotCallProvider();
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
                Title = "Stored Song",
                ArtistName = "Test Artist",
                DurationMs = 1000
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();

            if (seedLibraryFile)
            {
                db.LibraryFiles.Add(new LibraryFile
                {
                    TrackId = track.Id,
                    RelativePath = "Test Artist/Stored Song.mp3",
                    Format = "mp3",
                    SizeBytes = 1,
                    Sha256 = new string('a', 64)
                });
            }

            var job = new DownloadJob
            {
                TrackId = track.Id,
                Status = jobStatus,
                ProviderName = "must-not-call",
                SourceUrl = "fake://source",
                MatchScore = 0.9f
            };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var worker = serviceProvider.GetRequiredService<MusicSync.Worker.Worker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            DownloadJob? persisted = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                if (persisted.Status is DownloadJobStatus.Done or DownloadJobStatus.Failed)
                    break;
            }

            Assert.NotNull(persisted);
            Assert.Equal(DownloadJobStatus.Done, persisted.Status);
            Assert.Equal(expectedNote, persisted.Error);
            Assert.Equal(target, persisted.OutputPath);
            Assert.Equal(0, provider.FindCalls);
            Assert.Equal(0, provider.DownloadCalls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class MustNotCallProvider : IAcquisitionProvider
    {
        public string Name => "must-not-call";

        public int FindCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(
            TrackInfo track,
            int max,
            CancellationToken ct)
        {
            FindCalls++;
            throw new InvalidOperationException("The provider must not be called for an existing library file.");
        }

        public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
        {
            DownloadCalls++;
            throw new InvalidOperationException("The provider must not be called for an existing library file.");
        }
    }
}
