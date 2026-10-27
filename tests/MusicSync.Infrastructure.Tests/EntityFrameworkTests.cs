using Xunit;
using MusicSync.Infrastructure;

namespace MusicSync.Infrastructure.Tests;

public class EntityFrameworkTests
{
    [Fact]
    public void DownloadJob_HasRequiredProperties()
    {
        // Arrange
        var job = new DownloadJob
        {
            TrackId = 1,
            Status = "Queued",
            Attempts = 0
        };

        // Assert
        Assert.True(job.Id >= 0);
        Assert.Equal("Queued", job.Status);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public void Track_HasRequiredProperties()
    {
        // Arrange
        var track = new Track
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Track",
            ArtistName = "Test Artist"
        };

        // Assert
        Assert.True(track.Id >= 0);
        Assert.Equal("Test Track", track.Title);
        Assert.Equal("Test Artist", track.ArtistName);
    }

    [Fact]
    public void Album_HasRequiredProperties()
    {
        // Arrange
        var album = new Album
        {
            Mbid = Guid.NewGuid(),
            Title = "Test Album",
            Year = 2024
        };

        // Assert
        Assert.True(album.Id >= 0);
        Assert.Equal("Test Album", album.Title);
        Assert.Equal(2024, album.Year);
    }

    [Fact]
    public void Artist_HasRequiredProperties()
    {
        // Arrange
        var artist = new Artist
        {
            Mbid = Guid.NewGuid(),
            Name = "Test Artist"
        };

        // Assert
        Assert.True(artist.Id >= 0);
        Assert.Equal("Test Artist", artist.Name);
    }

    [Fact]
    public void LibraryFile_HasSha256ForDedupe()
    {
        // Arrange
        var file = new LibraryFile
        {
            TrackId = 1,
            RelativePath = "Artist/Album/01 - Song.mp3",
            Sha256 = "a".PadLeft(64, 'a')
        };

        // Assert
        Assert.Equal(64, file.Sha256.Length);
        Assert.Contains(file.RelativePath, "Artist");
    }

    [Fact]
    public void Setting_HasKeyValue()
    {
        // Arrange
        var setting = new Setting
        {
            Key = "Audio__Format",
            Value = "mp3"
        };

        // Assert
        Assert.Equal("Audio__Format", setting.Key);
        Assert.Equal("mp3", setting.Value);
    }

    [Fact]
    public void UserAccount_HasUsernameAndPassword()
    {
        // Arrange
        var account = new UserAccount
        {
            Username = "testuser",
            PasswordHash = "hashedpassword123"
        };

        // Assert
        Assert.Equal("testuser", account.Username);
        Assert.Equal("hashedpassword123", account.PasswordHash);
    }
}