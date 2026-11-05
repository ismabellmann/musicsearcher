using MusicSync.Core;
using MusicSync.Core.Interfaces;
using TagLib;

namespace MusicSync.Infrastructure;

/// <summary>
/// Writes ID3 tags and embedded front cover art with TagLibSharp. ffmpeg has already
/// produced the MP3; this runs before the file is moved into the library.
/// </summary>
public sealed class TagLibTagWriter : ITagWriter
{
    public Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        tag.Title = track.Title;

        var performers = (track.Artists.Count > 0
                ? track.Artists.Select(credit => credit.Name)
                : new[] { track.ArtistName ?? string.Empty })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (performers.Length > 0)
        {
            tag.Performers = performers;
            tag.AlbumArtists = performers;
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumTitle))
            tag.Album = track.AlbumTitle;

        if (track.Year is > 0)
            tag.Year = (uint)track.Year.Value;

        if (track.TrackNumber is > 0)
            tag.Track = (uint)track.TrackNumber.Value;

        if (track.DiscNumber is > 0)
            tag.Disc = (uint)track.DiscNumber.Value;

        if (track.RecordingMbid != Guid.Empty)
            tag.MusicBrainzTrackId = track.RecordingMbid.ToString();

        if (track.ReleaseGroupMbid is Guid releaseGroup && releaseGroup != Guid.Empty)
            tag.MusicBrainzReleaseGroupId = releaseGroup.ToString();

        if (track.GetPrimaryArtistMbid() is Guid artistMbid && artistMbid != Guid.Empty)
            tag.MusicBrainzArtistId = artistMbid.ToString();

        if (!string.IsNullOrWhiteSpace(track.Isrc))
            tag.ISRC = track.Isrc;

        if (coverArt is { Length: > 0 })
            tag.Pictures = CreatePictures(coverArt);

        file.Save();
        return Task.CompletedTask;
    }

    public Task WriteCoverAsync(string filePath, byte[] coverArt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (coverArt is not { Length: > 0 })
            return Task.CompletedTask;

        using var file = TagLib.File.Create(filePath);
        var otherPictures = file.Tag.Pictures
            .Where(picture => picture.Type != PictureType.FrontCover)
            .ToArray();
        file.Tag.Pictures = otherPictures.Concat(CreatePictures(coverArt)).ToArray();
        file.Save();
        return Task.CompletedTask;
    }

    private static IPicture[] CreatePictures(byte[] coverArt) => new IPicture[]
    {
        new Picture(new ByteVector(coverArt))
        {
            Type = PictureType.FrontCover,
            MimeType = DetectImageMime(coverArt)
        }
    };

    private static string DetectImageMime(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        return "image/jpeg";
    }
}
