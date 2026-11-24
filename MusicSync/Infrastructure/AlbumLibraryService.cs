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
                    group.Sum(item => item.file.SizeBytes));
            })
            .OrderBy(item => item.ArtistName)
            .ThenBy(item => item.Title)
            .ToList();
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
    long SizeBytes);
