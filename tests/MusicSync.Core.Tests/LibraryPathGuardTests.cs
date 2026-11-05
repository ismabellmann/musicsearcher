using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class LibraryPathGuardTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "musicsync-guard"));

    [Fact]
    public void ResolvesRelativePathInsideRoot()
    {
        var full = LibraryPathGuard.ResolveRelativePath(Root, "Pold/Hideout.mp3");
        Assert.Equal(Path.Combine(Root, "Pold", "Hideout.mp3"), full);
    }

    [Theory]
    [InlineData("../escape.mp3")]
    [InlineData("Pold/../../escape.mp3")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\escape.mp3")]
    [InlineData("")]
    public void RejectsPathsOutsideRoot(string relative)
    {
        Assert.ThrowsAny<ArgumentException>(() => LibraryPathGuard.ResolveRelativePath(Root, relative));
    }

    [Fact]
    public void NormalizeRelative_UsesForwardSlashes()
    {
        var relative = LibraryPathGuard.NormalizeRelative(Root, Path.Combine(Root, "Pold", "Hideout.mp3"));
        Assert.Equal("Pold/Hideout.mp3", relative);
    }
}

public class TrackDisplayTests
{
    [Fact]
    public void Title_ListsEveryCreditedArtist()
    {
        var track = new TrackInfo
        {
            Title = "Hideout",
            Artists = new[] { new ArtistCredit("Pold", Guid.Empty), new ArtistCredit("Guest", Guid.Empty) }
        };

        Assert.Equal("Hideout - Pold & Guest", TrackDisplay.Title(track));
    }

    [Fact]
    public void Title_FallsBackToArtistName()
    {
        Assert.Equal("Song - Solo", TrackDisplay.Title(new TrackInfo { Title = "Song", ArtistName = "Solo" }));
    }
}
