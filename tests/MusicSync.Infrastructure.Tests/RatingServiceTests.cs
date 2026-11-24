using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class RatingServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MusicSyncDbContext _db = null!;
    private RatingService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new RatingService(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Set_UpdatesSameTargetAndPreservesNotes()
    {
        await _service.SetAsync("track", "track:abc", 4, "Great", CancellationToken.None);
        await _service.SetAsync("track", "track:abc", 5, "Excellent", CancellationToken.None);

        var rating = await _service.GetAsync("track", "track:abc", CancellationToken.None);
        Assert.NotNull(rating);
        Assert.Equal(5, rating!.Value);
        Assert.Equal("Excellent", rating.Notes);
        Assert.Equal(1, await _db.Ratings.CountAsync());
    }

    [Fact]
    public async Task Delete_ClearsRating()
    {
        await _service.SetAsync("album", "album:abc", 3, null, CancellationToken.None);

        Assert.True(await _service.DeleteAsync("album", "album:abc", CancellationToken.None));
        Assert.Null(await _service.GetAsync("album", "album:abc", CancellationToken.None));
    }

    [Fact]
    public async Task Set_RejectsInvalidValues()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.SetAsync("track", "track:abc", 6, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetAsync("artist", "artist:abc", 2, null, CancellationToken.None));
    }

    [Fact]
    public async Task AlbumLibrary_AveragesPersonalTrackRatings()
    {
        var artist = new Artist { Name = "Artist" };
        var first = new Track { RecordingMbid = Guid.NewGuid(), Title = "One", ArtistName = "Artist", AlbumTitle = "Album", ReleaseGroupMbid = Guid.NewGuid() };
        var second = new Track { RecordingMbid = Guid.NewGuid(), Title = "Two", ArtistName = "Artist", AlbumTitle = "Album", ReleaseGroupMbid = first.ReleaseGroupMbid };
        first.TrackArtists.Add(new TrackArtist { Track = first, Artist = artist, Position = 0 });
        second.TrackArtists.Add(new TrackArtist { Track = second, Artist = artist, Position = 0 });
        _db.Tracks.AddRange(first, second);
        await _db.SaveChangesAsync();
        _db.LibraryFiles.AddRange(
            new LibraryFile { TrackId = first.Id, RelativePath = "Artist/Album/01 - One.mp3", SizeBytes = 1 },
            new LibraryFile { TrackId = second.Id, RelativePath = "Artist/Album/02 - Two.mp3", SizeBytes = 1 });
        await _db.SaveChangesAsync();
        await _service.SetAsync("track", RatingService.TrackKey(first.RecordingMbid), 4, null, CancellationToken.None);
        await _service.SetAsync("track", RatingService.TrackKey(second.RecordingMbid), 2, null, CancellationToken.None);

        var album = Assert.Single(await new AlbumLibraryService(_db).ListAsync(CancellationToken.None));
        Assert.Equal(3, album.PersonalRating);
        Assert.Equal(2, album.TrackCount);
    }
}
