using Microsoft.EntityFrameworkCore;
using MusicSync.Core;

namespace MusicSync.Infrastructure;

/// <summary>
/// Stores track metadata and its ordered artist credits. Position 0 is the primary artist.
/// </summary>
public static class TrackCatalog
{
    public static async Task<Track> GetOrCreateTrackAsync(MusicSyncDbContext db, TrackInfo info, CancellationToken ct)
    {
        var existing = await db.Tracks.SingleOrDefaultAsync(item => item.RecordingMbid == info.RecordingMbid, ct);
        if (existing is not null)
            return existing;

        var credits = GetCredits(info);
        var track = new Track
        {
            RecordingMbid = info.RecordingMbid,
            Title = info.Title,
            ArtistName = credits.Count > 0 ? string.Join(" & ", credits.Select(credit => credit.Name)) : string.Empty,
            AlbumTitle = info.AlbumTitle,
            ReleaseGroupMbid = info.ReleaseGroupMbid,
            Year = info.Year,
            Isrc = info.Isrc,
            DurationMs = info.DurationMs
        };
        db.Tracks.Add(track);

        for (var position = 0; position < credits.Count; position++)
        {
            var artist = await GetOrCreateArtistAsync(db, credits[position], ct);
            track.TrackArtists.Add(new TrackArtist { Track = track, Artist = artist, Position = position });
        }

        await db.SaveChangesAsync(ct);
        return track;
    }

    public static Task<Track?> FindWithArtistsAsync(MusicSyncDbContext db, int trackId, CancellationToken ct)
    {
        return db.Tracks
            .Include(item => item.TrackArtists)
            .ThenInclude(link => link.Artist)
            .SingleOrDefaultAsync(item => item.Id == trackId, ct);
    }

    public static TrackInfo ToTrackInfo(Track track) => new()
    {
        RecordingMbid = track.RecordingMbid,
        Title = track.Title,
        ArtistName = track.ArtistName,
        AlbumTitle = track.AlbumTitle,
        ReleaseGroupMbid = track.ReleaseGroupMbid,
        Year = track.Year,
        Isrc = track.Isrc,
        DurationMs = track.DurationMs,
        Artists = track.TrackArtists
            .OrderBy(link => link.Position)
            .Select(link => new ArtistCredit(link.Artist.Name, link.Artist.Mbid ?? Guid.Empty))
            .ToList()
    };

    private static List<ArtistCredit> GetCredits(TrackInfo info)
    {
        var named = info.Artists.Where(credit => !string.IsNullOrWhiteSpace(credit.Name)).ToList();
        if (named.Count > 0)
            return named;

        return string.IsNullOrWhiteSpace(info.ArtistName)
            ? new List<ArtistCredit>()
            : new List<ArtistCredit> { new(info.ArtistName, Guid.Empty) };
    }

    private static async Task<Artist> GetOrCreateArtistAsync(MusicSyncDbContext db, ArtistCredit credit, CancellationToken ct)
    {
        var name = credit.Name.Trim();
        var mbid = credit.Mbid == Guid.Empty ? (Guid?)null : credit.Mbid;

        var artist = db.Artists.Local.FirstOrDefault(item => MatchesCredit(item, name, mbid))
            ?? (mbid is null
                ? await db.Artists.FirstOrDefaultAsync(item => item.Mbid == null && item.Name == name, ct)
                : await db.Artists.FirstOrDefaultAsync(item => item.Mbid == mbid, ct));

        if (artist is null)
        {
            artist = new Artist { Mbid = mbid, Name = name };
            db.Artists.Add(artist);
        }

        return artist;
    }

    private static bool MatchesCredit(Artist artist, string name, Guid? mbid)
    {
        return mbid is null
            ? artist.Mbid is null && artist.Name == name
            : artist.Mbid == mbid;
    }
}
