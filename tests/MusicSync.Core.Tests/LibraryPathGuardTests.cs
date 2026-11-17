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

    [Fact]
    public void RejectsExistingSymlinkInsideRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var root = Path.Combine(Path.GetTempPath(), "musicsync-symlink-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "musicsync-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            Assert.ThrowsAny<ArgumentException>(() => LibraryPathGuard.ResolveRelativePath(root, "linked/file.mp3"));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
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
