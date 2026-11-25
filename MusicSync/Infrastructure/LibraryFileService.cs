using Microsoft.EntityFrameworkCore;
using MusicSync.Core;

namespace MusicSync.Infrastructure;

public sealed class LibraryFileService
{
    private readonly MusicSyncDbContext _db;
    private readonly LibraryOptions _library;

    public LibraryFileService(MusicSyncDbContext db, LibraryOptions library)
    {
        _db = db;
        _library = library;
    }

    public async Task<LibraryFilePage> ListAsync(
        string? query,
        string? origin,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var files = _db.LibraryFiles.AsNoTracking();
        var tracks = _db.Tracks.AsNoTracking();

        var joined = from file in files
                     join track in tracks on file.TrackId equals track.Id
                     join imported in _db.LibraryImports.AsNoTracking()
                         on new { file.TrackId, file.RelativePath }
                         equals new { TrackId = imported.TrackId!.Value, imported.RelativePath }
                         into importedFiles
                     from imported in importedFiles.DefaultIfEmpty()
                     select new { file, track, imported };

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            joined = joined.Where(item =>
                EF.Functions.Like(item.track.Title, pattern)
                || EF.Functions.Like(item.track.ArtistName, pattern)
                || (item.track.AlbumTitle != null && EF.Functions.Like(item.track.AlbumTitle, pattern)));
        }

        if (string.Equals(origin, "imported", StringComparison.OrdinalIgnoreCase))
            joined = joined.Where(item => item.imported != null);
        else if (string.Equals(origin, "downloaded", StringComparison.OrdinalIgnoreCase))
            joined = joined.Where(item => item.imported == null);

        var total = await joined.CountAsync(ct);
        var rows = await joined
            .OrderBy(item => item.file.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new LibraryFileView(
                item.file.Id,
                item.file.TrackId,
                item.file.RelativePath,
                item.track.RecordingMbid,
                item.track.Title,
                item.track.ArtistName,
                item.track.AlbumTitle,
                item.track.ReleaseGroupMbid,
                item.track.Year,
                item.track.Isrc,
                item.track.DurationMs,
                item.file.Format,
                item.file.Bitrate,
                item.file.SizeBytes,
                item.file.Sha256,
                item.file.AddedAt,
                item.imported != null ? "imported" : "downloaded",
                item.track.SourceUrl,
                Array.Empty<string>(), item.track.MusicBrainzRating, 0, null, item.track.Genre))
            .ToListAsync(ct);

        var trackIds = rows.Select(item => item.TrackId).ToArray();
        var recordingKeys = await _db.Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .Select(track => new { track.Id, Key = RatingService.TrackKey(track.RecordingMbid) })
            .ToListAsync(ct);
        var keys = recordingKeys.Select(item => item.Key).ToArray();
        var ratings = await _db.Ratings.AsNoTracking()
            .Where(item => item.TargetType == "track" && keys.Contains(item.TargetKey))
            .ToDictionaryAsync(item => item.TargetKey, ct);
        rows = rows.Select(row =>
        {
            var key = recordingKeys.FirstOrDefault(item => item.Id == row.TrackId)?.Key;
            ratings.TryGetValue(key ?? string.Empty, out var rating);
            return row with { PersonalRating = rating?.Value ?? 0, PersonalNotes = rating?.Notes };
        }).ToList();
        return new LibraryFilePage(rows, page, pageSize, total);
    }

    public async Task<LibraryFileView?> GetAsync(int id, CancellationToken ct)
    {
        var result = await (from file in _db.LibraryFiles.AsNoTracking()
                            join track in _db.Tracks.AsNoTracking() on file.TrackId equals track.Id
                            join imported in _db.LibraryImports.AsNoTracking()
                                on new { file.TrackId, file.RelativePath }
                                equals new { TrackId = imported.TrackId!.Value, imported.RelativePath }
                                into importedFiles
                            from imported in importedFiles.DefaultIfEmpty()
                            where file.Id == id
                            select new LibraryFileView(
                                file.Id, file.TrackId, file.RelativePath, track.RecordingMbid, track.Title,
                                track.ArtistName, track.AlbumTitle, track.ReleaseGroupMbid,
                                track.Year, track.Isrc, track.DurationMs, file.Format,
                                file.Bitrate, file.SizeBytes, file.Sha256, file.AddedAt,
                                imported != null ? "imported" : "downloaded", track.SourceUrl,
                                Array.Empty<string>(), track.MusicBrainzRating, 0, null, track.Genre))
            .SingleOrDefaultAsync(ct);
        if (result is not null)
        {
            result = result with
            {
                Artists = await _db.TrackArtists.AsNoTracking()
                    .Where(item => item.TrackId == result.TrackId)
                    .OrderBy(item => item.Position)
                    .Join(_db.Artists.AsNoTracking(), item => item.ArtistId, artist => artist.Id, (_, artist) => artist.Name)
                    .ToListAsync(ct)
            };
        }
        if (result is not null)
        {
            var rating = await _db.Ratings.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TargetType == "track" && item.TargetKey == RatingService.TrackKey(result.RecordingMbid), ct);
            result = result with { PersonalRating = rating?.Value ?? 0, PersonalNotes = rating?.Notes };
        }
        return result;
    }

    public async Task<DeleteLibraryFileOutcome> DeleteAsync(int id, CancellationToken ct)
    {
        var file = await _db.LibraryFiles.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (file is null)
            return DeleteLibraryFileOutcome.NotFound;

        var absolutePath = LibraryPathGuard.ResolveRelativePath(_library.Root, file.RelativePath);
        if (File.Exists(absolutePath))
            File.Delete(absolutePath);
        else if (Directory.Exists(absolutePath))
            return DeleteLibraryFileOutcome.InvalidFile;

        var imported = await _db.LibraryImports.SingleOrDefaultAsync(
            item => item.TrackId == file.TrackId && item.RelativePath == file.RelativePath,
            ct);
        if (imported is not null)
        {
            imported.Status = LibraryImportStatus.Pending;
            imported.TrackId = null;
            imported.SourceUrl = null;
            imported.IdentifiedAt = null;
            imported.Error = null;
        }

        _db.LibraryFiles.Remove(file);
        await _db.SaveChangesAsync(ct);
        PruneEmptyDirectories(Path.GetDirectoryName(absolutePath), Path.GetFullPath(_library.Root));
        return DeleteLibraryFileOutcome.Deleted;
    }

    private static void PruneEmptyDirectories(string? directory, string root)
    {
        var current = directory;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        while (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.Ordinal))
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                break;
            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }
}

public sealed record LibraryFilePage(
    IReadOnlyList<LibraryFileView> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record LibraryFileView(
    int Id,
    int TrackId,
    string RelativePath,
    Guid RecordingMbid,
    string Title,
    string ArtistName,
    string? AlbumTitle,
    Guid? ReleaseGroupMbid,
    int? Year,
    string? Isrc,
    int DurationMs,
    string? Format,
    int? Bitrate,
    long SizeBytes,
    string Sha256,
    DateTime AddedAt,
    string Origin,
    string? SourceUrl,
    IReadOnlyList<string> Artists,
    double? MusicBrainzRating = null,
    int PersonalRating = 0,
    string? PersonalNotes = null,
    string? Genre = null);

public enum DeleteLibraryFileOutcome
{
    Deleted,
    NotFound,
    InvalidFile
}
