using System.Diagnostics;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class TagLibTagWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "musicsync-tags-" + Guid.NewGuid().ToString("N"));

    public TagLibTagWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_WritesTextTagsAndEmbeddedCover()
    {
        var mp3 = Path.Combine(_directory, "track.mp3");
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1", mp3);

        var recordingMbid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var releaseGroupMbid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var artistMbid = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var track = new TrackInfo
        {
            RecordingMbid = recordingMbid,
            Title = "Tagged Song",
            ArtistName = "First Artist",
            Artists = new[]
            {
                new ArtistCredit { Name = "First Artist", Mbid = artistMbid },
                new ArtistCredit { Name = "Second Artist", Mbid = Guid.Parse("44444444-4444-4444-4444-444444444444") }
            },
            AlbumTitle = "Tagged Album",
            ReleaseGroupMbid = releaseGroupMbid,
            Year = 2021,
            TrackNumber = 7,
            DiscNumber = 2,
            Isrc = "US-AAA-21-00007"
        };

        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        await new TagLibTagWriter().WriteAsync(mp3, track, cover, CancellationToken.None);

        using var file = TagLib.File.Create(mp3);
        Assert.Equal("Tagged Song", file.Tag.Title);
        Assert.Equal(new[] { "First Artist", "Second Artist" }, file.Tag.Performers);
        Assert.Equal(new[] { "First Artist", "Second Artist" }, file.Tag.AlbumArtists);
        Assert.Equal("Tagged Album", file.Tag.Album);
        Assert.Equal(2021u, file.Tag.Year);
        Assert.Equal(7u, file.Tag.Track);
        Assert.Equal(2u, file.Tag.Disc);
        Assert.Equal(recordingMbid.ToString(), file.Tag.MusicBrainzTrackId);
        Assert.Equal(releaseGroupMbid.ToString(), file.Tag.MusicBrainzReleaseGroupId);
        Assert.Equal(artistMbid.ToString(), file.Tag.MusicBrainzArtistId);
        Assert.Equal("US-AAA-21-00007", file.Tag.ISRC);

        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("image/jpeg", picture.MimeType);
        Assert.Equal(cover, picture.Data.Data);
    }

    [Fact]
    public async Task WriteCoverAsync_ReplacesFrontCoverWithoutChangingTextTags()
    {
        var mp3 = Path.Combine(_directory, "cover-only.mp3");
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1", mp3);

        var writer = new TagLibTagWriter();
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Existing Title",
            ArtistName = "Existing Artist",
            AlbumTitle = "Existing Album",
            Year = 1999
        };

        await writer.WriteAsync(mp3, track, null, CancellationToken.None);
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        await writer.WriteCoverAsync(mp3, cover, CancellationToken.None);

        using var file = TagLib.File.Create(mp3);
        Assert.Equal("Existing Title", file.Tag.Title);
        Assert.Equal("Existing Artist", file.Tag.FirstPerformer);
        Assert.Equal("Existing Album", file.Tag.Album);
        Assert.Equal(1999u, file.Tag.Year);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal(cover, picture.Data.Data);
    }

    [Fact]
    public async Task WriteAsync_WithoutCoverKeepsNoPictures()
    {
        var mp3 = Path.Combine(_directory, "plain.mp3");
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1", mp3);

        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Plain Song",
            ArtistName = "Solo Artist"
        };

        await new TagLibTagWriter().WriteAsync(mp3, track, null, CancellationToken.None);

        using var file = TagLib.File.Create(mp3);
        Assert.Equal("Plain Song", file.Tag.Title);
        Assert.Equal(new[] { "Solo Artist" }, file.Tag.Performers);
        Assert.Empty(file.Tag.Pictures);
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
