using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class WorkerApprovedJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-approved-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ApprovedJob_DownloadsTranscodesStoresAndMarksDone()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var stagingDirectory = Path.Combine(_root, "staging");
        var downloadWorkRoot = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_root);
        var tone = Path.Combine(_root, "tone.wav");
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=330:duration=2", tone);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(new LibraryOptions { Root = libraryRoot, StagingDirectory = stagingDirectory });
        services.AddSingleton(new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()));
        services.AddSingleton<LibraryPathResolver>();
        services.AddSingleton<IMatchScorer, TrackScorer>();
        services.AddSingleton<IAcquisitionProvider>(new FakeFileProvider(tone, downloadWorkRoot));
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
                Title = "Approved Song",
                ArtistName = "Test Artist",
                DurationMs = 2000
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();

            var job = new DownloadJob
            {
                TrackId = track.Id,
                Status = DownloadJobStatus.Approved,
                ProviderName = "fake",
                SourceUrl = "fake://approved-source",
                MatchScore = 0.75f
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
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                persisted = await db.DownloadJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                if (persisted.Status is "Done" or "Failed")
                    break;
            }

            Assert.NotNull(persisted);
            Assert.Equal("Done", persisted.Status);
            Assert.Null(persisted.Error);
            Assert.Equal(Path.Combine(libraryRoot, "Test Artist", "Approved Song.mp3"), persisted.OutputPath);
            Assert.True(File.Exists(persisted.OutputPath));
            Assert.Equal(0.75f, persisted.MatchScore!.Value, 3);

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
                var libraryFile = await db.LibraryFiles.AsNoTracking().SingleAsync();
                Assert.Equal(Path.Combine("Test Artist", "Approved Song.mp3"), libraryFile.RelativePath);
                Assert.Equal("mp3", libraryFile.Format);
                Assert.Equal(64, libraryFile.Sha256.Length);
            }

            Assert.Empty(Directory.GetFiles(stagingDirectory));
            Assert.False(Directory.Exists(downloadWorkRoot) && Directory.GetDirectories(downloadWorkRoot).Length > 0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeFileProvider : IAcquisitionProvider
    {
        private readonly string _sourceFile;
        private readonly string _workRoot;

        public FakeFileProvider(string sourceFile, string workRoot)
        {
            _sourceFile = sourceFile;
            _workRoot = workRoot;
        }

        public string Name => "fake";

        public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceCandidate>>(Array.Empty<SourceCandidate>());

        public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
        {
            var directory = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "source.wav");
            File.Copy(_sourceFile, target);
            return Task.FromResult(new DownloadedFile { FilePath = target, IsTemporary = true });
        }
    }

    private static async Task RunToolAsync(string fileName, params string[] arguments)
    {
        var startInfo = ExternalProcess.CreateStartInfo(fileName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var (exitCode, _, stderr) = await ExternalProcess.RunAsync(startInfo, CancellationToken.None);
        Assert.True(exitCode == 0, $"{fileName} failed: {stderr}");
    }
}
