using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class LibraryFileServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-library-files-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MusicSyncDbContext _db = null!;
    private LibraryFileService _service = null!;
    private int _importedId;
    private int _downloadedId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Artist", "Album"));
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        var importedTrack = new Track
        {
            RecordingMbid = Guid.NewGuid(), Title = "Imported Song", ArtistName = "Artist", AlbumTitle = "Album", DurationMs = 1000
        };
        var downloadedTrack = new Track
        {
            RecordingMbid = Guid.NewGuid(), Title = "Downloaded Song", ArtistName = "Artist", DurationMs = 2000
        };
        _db.Tracks.AddRange(importedTrack, downloadedTrack);
        await _db.SaveChangesAsync();

        var importedPath = "Artist/Album/01 - Imported Song.mp3";
        var downloadedPath = "Artist/Downloaded Song.mp3";
        await File.WriteAllTextAsync(Path.Combine(_root, importedPath.Replace('/', Path.DirectorySeparatorChar)), "imported");
        await File.WriteAllTextAsync(Path.Combine(_root, downloadedPath.Replace('/', Path.DirectorySeparatorChar)), "downloaded");

        _db.LibraryFiles.AddRange(
            new LibraryFile { TrackId = importedTrack.Id, RelativePath = importedPath, Format = "mp3", SizeBytes = 8, Sha256 = new string('a', 64) },
            new LibraryFile { TrackId = downloadedTrack.Id, RelativePath = downloadedPath, Format = "mp3", SizeBytes = 9, Sha256 = new string('b', 64) });
        _db.LibraryImports.Add(new LibraryImport
        {
            RelativePath = importedPath, ArtistName = "Artist", Title = "Imported Song", AlbumTitle = "Album",
            Status = LibraryImportStatus.Imported, TrackId = importedTrack.Id, SourceUrl = "musicbrainz://recording/test",
            IdentifiedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _importedId = await _db.LibraryFiles.Where(item => item.TrackId == importedTrack.Id).Select(item => item.Id).SingleAsync();
        _downloadedId = await _db.LibraryFiles.Where(item => item.TrackId == downloadedTrack.Id).Select(item => item.Id).SingleAsync();
        _service = new LibraryFileService(_db, new LibraryOptions { Root = _root });
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task List_FiltersByOriginAndQuery()
    {
        var imported = await _service.ListAsync("Imported", "imported", 1, 50, CancellationToken.None);
        var downloaded = await _service.ListAsync("Downloaded", "downloaded", 1, 50, CancellationToken.None);

        Assert.Equal(1, imported.TotalCount);
        Assert.Equal("imported", Assert.Single(imported.Items).Origin);
        Assert.Equal(1, downloaded.TotalCount);
        Assert.Equal("downloaded", Assert.Single(downloaded.Items).Origin);
    }

    [Fact]
    public async Task Delete_ResetsImportedRecordAndPrunesEmptyDirectory()
    {
        var outcome = await _service.DeleteAsync(_importedId, CancellationToken.None);

        Assert.Equal(DeleteLibraryFileOutcome.Deleted, outcome);
        Assert.False(File.Exists(Path.Combine(_root, "Artist", "Album", "01 - Imported Song.mp3")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Artist", "Album")));
        var import = await _db.LibraryImports.SingleAsync();
        Assert.Equal(LibraryImportStatus.Pending, import.Status);
        Assert.Null(import.TrackId);
        Assert.Equal(1, await _db.LibraryFiles.CountAsync());
    }

    [Fact]
    public async Task Delete_MissingFileStillRemovesDatabaseRecord()
    {
        File.Delete(Path.Combine(_root, "Artist", "Downloaded Song.mp3"));

        var outcome = await _service.DeleteAsync(_downloadedId, CancellationToken.None);

        Assert.Equal(DeleteLibraryFileOutcome.Deleted, outcome);
        Assert.False(await _db.LibraryFiles.AnyAsync(item => item.Id == _downloadedId));
    }
}
