using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class AnalyticsServiceTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MusicSyncDbContext _db = null!;
    private AnalyticsService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new AnalyticsService(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose() { }

    [Fact]
    public async Task EmptyLibrary_ReturnsZeroSummary()
    {
        var summary = await _service.GetSummaryAsync(10, CancellationToken.None);

        Assert.Equal(0, summary.TotalFiles);
        Assert.Equal(0, summary.TotalBytes);
        Assert.Equal(0, summary.SuccessRate);
        Assert.Empty(summary.TopArtists);
    }

    [Fact]
    public async Task SummaryCountsLibraryOriginsJobsAndArtists()
    {
        var artist = new Artist { Name = "Analytics Artist" };
        var track = new Track
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Analytics Song",
            ArtistName = artist.Name,
            AlbumTitle = "Analytics Album",
            DurationMs = 1000
        };
        track.TrackArtists.Add(new TrackArtist { Track = track, Artist = artist, Position = 0 });
        _db.Tracks.Add(track);
        await _db.SaveChangesAsync();

        _db.LibraryFiles.Add(new LibraryFile
        {
            TrackId = track.Id,
            RelativePath = "Analytics Artist/Analytics Song.mp3",
            SizeBytes = 123,
            Format = "mp3",
            Sha256 = new string('a', 64)
        });
        _db.LibraryImports.Add(new LibraryImport
        {
            RelativePath = "Analytics Artist/Analytics Song.mp3",
            ArtistName = artist.Name,
            Title = track.Title,
            Status = LibraryImportStatus.Imported,
            TrackId = track.Id
        });
        _db.DownloadJobs.AddRange(
            new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Done, MatchScore = .9f },
            new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Failed, MatchScore = .3f },
            new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.NeedsReview });
        await _db.SaveChangesAsync();

        var summary = await _service.GetSummaryAsync(10, CancellationToken.None);

        Assert.Equal(1, summary.TotalFiles);
        Assert.Equal(123, summary.TotalBytes);
        Assert.Equal(1, summary.ImportedFiles);
        Assert.Equal(0, summary.DownloadedFiles);
        Assert.Equal(1, summary.TrackCount);
        Assert.Equal(1, summary.ArtistCount);
        Assert.Equal(1, summary.AlbumCount);
        Assert.Equal(1, summary.CompletedJobs);
        Assert.Equal(1, summary.FailedJobs);
        Assert.Equal(.5, summary.SuccessRate);
        Assert.Equal(1, summary.NeedsReviewCount);
        Assert.Equal("Analytics Artist", Assert.Single(summary.TopArtists).ArtistName);
    }
}
