using Xunit;
using MusicSync.Core;

namespace MusicSync.Core.Tests;

public class LibraryPathResolverTests
{
    private readonly LibraryPathResolver _resolver;

    public LibraryPathResolverTests()
    {
        _resolver = new LibraryPathResolver();
    }

    [Fact]
    public void Resolve_AlbumTrack_ReturnsCorrectPathFormat()
    {
        // Arrange
        var track = new TrackInfo
        {
            ArtistName = "Artist Name",
            Title = "Song Title",
            AlbumTitle = "Album Name",
            TrackNumber = 1
        };
        const string extension = ".mp3";

        // Act
        var result = _resolver.Resolve(track, asSingle: false, extension);

        // Assert
        Assert.Contains("Artist Name/Album Name/01 - Song Title.mp3", result);
        Assert.DoesNotContain(result, "Invalid");
    }

    [Fact]
    public void Resolve_SingleTrack_ReturnsCorrectPathFormat()
    {
        // Arrange
        var track = new TrackInfo
        {
            ArtistName = "Artist Name",
            Title = "Song Title",
            AlbumTitle = null
        };
        const string extension = ".flac";

        // Act
        var result = _resolver.Resolve(track, asSingle: true, extension);

        // Assert
        Assert.Contains("Artist Name/Song Title.flac", result);
        Assert.DoesNotContain(result, "Album");
    }

    [Fact]
    public void Resolve_TrackWithSpecialChars_Sanitized()
    {
        // Arrange
        var track = new TrackInfo
        {
            ArtistName = "Artist <Name>",
            Title = "Song > Title",
            AlbumTitle = "Alb<um>"
        };
        const string extension = ".mp3";

        // Act
        var result = _resolver.Resolve(track, asSingle: false, extension);

        // Assert
        Assert.DoesNotContain(result, "<");
        Assert.DoesNotContain(result, ">");
        Assert.DoesNotContain(result, "|");
    }

    [Fact]
    public void Resolve_PrimaryArtistUsedForPath()
    {
        // Arrange - verifies the "composite artist C" bug fix
        var track = new TrackInfo
        {
            Artists = new List<ArtistCredit>
            {
                new ArtistCredit("Artist A", Guid.Parse("C6E3A640-E0F5-47A7-83A8-48D1E2A6A6AC")),
                new ArtistCredit("Artist B", Guid.Parse("A2E5B8D0-F4C5-4F2A-9B8E-6C4D8E2A4B6C"))
            },
            Title = "Song",
            AlbumTitle = null
        };
        const string extension = ".mp3";

        // Act
        var result = _resolver.Resolve(track, asSingle: true, extension);

        // Assert - should use primary artist only
        Assert.Contains("Artist A/Song.mp3", result);
        Assert.DoesNotContain(result, "Artist A & Artist B");
    }
}