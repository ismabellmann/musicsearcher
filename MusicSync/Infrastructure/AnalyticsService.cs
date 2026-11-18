using Microsoft.EntityFrameworkCore;

namespace MusicSync.Infrastructure;

public sealed class AnalyticsService
{
    private readonly MusicSyncDbContext _db;

    public AnalyticsService(MusicSyncDbContext db) => _db = db;

    public async Task<AnalyticsSummary> GetSummaryAsync(int topArtistLimit, CancellationToken ct)
    {
        topArtistLimit = Math.Clamp(topArtistLimit, 1, 50);

        var totalFiles = await _db.LibraryFiles.CountAsync(ct);
        var totalBytes = await _db.LibraryFiles.Select(file => (long?)file.SizeBytes).SumAsync(ct) ?? 0;
        var importedFiles = await (from file in _db.LibraryFiles
                                   join import in _db.LibraryImports
                                       on new { file.TrackId, file.RelativePath }
                                       equals new { TrackId = import.TrackId!.Value, import.RelativePath }
                                   select file.Id).Distinct().CountAsync(ct);
        var downloadedFiles = totalFiles - importedFiles;
        var trackCount = await _db.LibraryFiles.Select(file => file.TrackId).Distinct().CountAsync(ct);
        var artistCount = await (from file in _db.LibraryFiles
                                 join link in _db.TrackArtists on file.TrackId equals link.TrackId
                                 select link.ArtistId).Distinct().CountAsync(ct);
        var albumCount = await _db.LibraryFiles
            .Join(_db.Tracks, file => file.TrackId, track => track.Id, (file, track) => track.AlbumTitle)
            .Where(title => title != null && title != "")
            .Distinct()
            .CountAsync(ct);

        var jobsByStatus = await _db.DownloadJobs
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, ct);
        var completed = jobsByStatus.GetValueOrDefault(DownloadJobStatus.Done);
        var failed = jobsByStatus.GetValueOrDefault(DownloadJobStatus.Failed);
        var finished = completed + failed;
        var averageScore = await _db.DownloadJobs
            .Where(job => job.MatchScore != null)
            .Select(job => (double?)job.MatchScore)
            .AverageAsync(ct) ?? 0;
        var needsReview = jobsByStatus.GetValueOrDefault(DownloadJobStatus.NeedsReview);
        var missingMetadata = await (from file in _db.LibraryFiles
                                     join track in _db.Tracks on file.TrackId equals track.Id
                                     where track.ArtistName == "" || track.AlbumTitle == null
                                     select file.Id).Distinct().CountAsync(ct);

        var topArtists = await (from file in _db.LibraryFiles
                                join link in _db.TrackArtists on file.TrackId equals link.TrackId
                                join artist in _db.Artists on link.ArtistId equals artist.Id
                                group file by new { artist.Id, artist.Name } into grouped
                                orderby grouped.Count() descending, grouped.Key.Name
                                select new TopArtistSummary(grouped.Key.Name, grouped.Select(file => file.Id).Distinct().Count()))
            .Take(topArtistLimit)
            .ToListAsync(ct);

        return new AnalyticsSummary(
            totalFiles, totalBytes, importedFiles, downloadedFiles, trackCount, artistCount, albumCount,
            jobsByStatus, completed, failed, finished == 0 ? 0 : (double)completed / finished,
            averageScore, needsReview, missingMetadata, topArtists);
    }
}

public sealed record AnalyticsSummary(
    int TotalFiles,
    long TotalBytes,
    int ImportedFiles,
    int DownloadedFiles,
    int TrackCount,
    int ArtistCount,
    int AlbumCount,
    IReadOnlyDictionary<string, int> JobsByStatus,
    int CompletedJobs,
    int FailedJobs,
    double SuccessRate,
    double AverageMatchScore,
    int NeedsReviewCount,
    int MissingMetadataCount,
    IReadOnlyList<TopArtistSummary> TopArtists);

public sealed record TopArtistSummary(string ArtistName, int FileCount);
