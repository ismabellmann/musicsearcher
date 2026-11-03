using Xunit;
using MusicSync.Core;

namespace MusicSync.Core.Tests;

public class LibraryPathContainmentTests
{
    private readonly LibraryPathResolver _resolver = new();
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "musicsync-library"));

    [Fact]
    public void ResolveWithinRoot_SingleTrack_ReturnsPathUnderRoot()
    {
        var track = new TrackInfo { ArtistName = "Artist Name", Title = "Song Title", DurationMs = 1000 };

        var result = _resolver.ResolveWithinRoot(_root, track, asSingle: true, ".mp3");

        Assert.Equal(Path.Combine(_root, "Artist Name", "Song Title.mp3"), result);
    }

    [Fact]
    public void ResolveWithinRoot_ArtistWithParentSegment_IsRejected()
    {
        var track = new TrackInfo { ArtistName = "..", Title = "Song", DurationMs = 1000 };

        Assert.Throws<InvalidOperationException>(() =>
            _resolver.ResolveWithinRoot(_root, track, asSingle: true, ".mp3"));
    }

    [Fact]
    public void ResolveWithinRoot_ArtistAbsolutePath_IsSanitizedAndStaysUnderRoot()
    {
        var track = new TrackInfo { ArtistName = "/etc", Title = "Song", DurationMs = 1000 };

        var result = _resolver.ResolveWithinRoot(_root, track, asSingle: true, ".mp3");

        Assert.StartsWith(_root + Path.DirectorySeparatorChar, result, StringComparison.Ordinal);
    }
}
