using Microsoft.EntityFrameworkCore;

namespace MusicSync.Infrastructure;

public sealed class AlbumLibraryService
{
    private readonly MusicSyncDbContext _db;
    public AlbumLibraryService(MusicSyncDbContext db) => _db = db;

    public async Task<IReadOnlyList<AlbumLibraryView>> ListAsync(CancellationToken ct)
    {
        var rows = await (from file in _db.LibraryFiles.AsNoTracking()
                          join track in _db.Tracks.AsNoTracking() on file.TrackId equals track.Id
                          where track.AlbumTitle != null && track.AlbumTitle != ""
                          select new { file, track })
            .ToListAsync(ct);

        var trackIds = rows.Select(row => row.track.Id).Distinct().ToArray();
        var ratings = await _db.Ratings.AsNoTracking()
            .Where(item => item.TargetType == "track")
            .ToListAsync(ct);
        var ratingByKey = ratings.ToDictionary(item => item.TargetKey, StringComparer.Ordinal);

        return rows.GroupBy(row => row.track.ReleaseGroupMbid is Guid mbid
                ? $"album:{mbid:D}"
                : $"album:fallback:{Normalize(row.track.ArtistName)}:{Normalize(row.track.AlbumTitle!)}")
            .Select(group =>
            {
                var tracks = group.Select(item => item.track).DistinctBy(item => item.Id).ToList();
                var personalScores = tracks.Select(track => ratingByKey.TryGetValue(RatingService.TrackKey(track.RecordingMbid), out var rating) ? rating.Value : 0).ToList();
                var musicBrainzScores = tracks.Where(track => track.MusicBrainzRating.HasValue).Select(track => track.MusicBrainzRating!.Value).ToList();
                var genre = tracks.Select(track => track.Genre).Where(value => !string.IsNullOrWhiteSpace(value))
                    .SelectMany(value => value!.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .FirstOrDefault();
                var first = tracks[0];
                return new AlbumLibraryView(
                    group.Key,
                    first.AlbumTitle!,
                    first.ArtistName,
                    first.Year,
                    first.ReleaseGroupMbid,
                    tracks.Count,
                    group.Select(item => item.file.Id).Distinct().Count(),
                    tracks.Count == 0 ? 0 : (double)group.Select(item => item.file.Id).Distinct().Count() / tracks.Count,
                    musicBrainzScores.Count == 0 ? null : musicBrainzScores.Average(),
                    personalScores.Count == 0 ? 0 : personalScores.Average(),
                    group.Sum(item => item.file.SizeBytes), genre);
            })
            .OrderBy(item => item.ArtistName)
            .ThenBy(item => item.Title)
            .ToList();
    }

    public async Task<AlbumLibraryDetail?> GetAsync(string key, CancellationToken ct)
    {
        var albums = await ListAsync(ct);
        var album = albums.SingleOrDefault(item => item.AlbumKey == key);
        if (album is null) return null;

        var rows = await (from file in _db.LibraryFiles.AsNoTracking()
                          join track in _db.Tracks.AsNoTracking() on file.TrackId equals track.Id
                          where track.AlbumTitle == album.Title && track.ArtistName == album.ArtistName
                          select new { file, track })
            .OrderBy(item => item.track.Year).ThenBy(item => item.track.Title)
            .ToListAsync(ct);
        var ids = rows.Select(item => item.track.RecordingMbid).ToArray();
        var ratings = await _db.Ratings.AsNoTracking()
            .Where(item => item.TargetType == "track" && ids.Select(id => RatingService.TrackKey(id)).Contains(item.TargetKey))
            .ToDictionaryAsync(item => item.TargetKey, ct);

        return new AlbumLibraryDetail(
            album,
            rows.Select(item => new AlbumTrackView(
                item.track.Id, item.track.Title, item.track.ArtistName, item.file.RelativePath,
                item.track.MusicBrainzRating,
                ratings.TryGetValue(RatingService.TrackKey(item.track.RecordingMbid), out var rating) ? rating.Value : 0)).ToList());
    }

    private static string Normalize(string value) => string.Join('-', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed record AlbumLibraryView(
    string AlbumKey,
    string Title,
    string ArtistName,
    int? Year,
    Guid? ReleaseGroupMbid,
    int TrackCount,
    int OwnedFileCount,
    double Completeness,
    double? MusicBrainzRating,
    double PersonalRating,
    long SizeBytes,
    string? Genre);

public sealed record AlbumLibraryDetail(AlbumLibraryView Album, IReadOnlyList<AlbumTrackView> Tracks);
public sealed record AlbumTrackView(int TrackId, string Title, string ArtistName, string RelativePath, double? MusicBrainzRating, int PersonalRating);
