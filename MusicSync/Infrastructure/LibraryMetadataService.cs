using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

public sealed class LibraryMetadataService
{
    private readonly MusicSyncDbContext _db;
    private readonly IMetadataProvider _metadata;

    public LibraryMetadataService(MusicSyncDbContext db, IMetadataProvider metadata)
    {
        _db = db; _metadata = metadata;
    }

    public async Task<MetadataRefreshSummary> RefreshAsync(IReadOnlyCollection<int>? fileIds, CancellationToken ct)
    {
        var query = _db.LibraryFiles.AsQueryable();
        if (fileIds is not null) query = query.Where(file => fileIds.Contains(file.Id));
        var trackIds = await query.Select(file => file.TrackId).Distinct().ToListAsync(ct);
        var updated = 0; var unchanged = 0; var skipped = 0; var failed = 0;

        foreach (var trackId in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            var track = await _db.Tracks.SingleAsync(item => item.Id == trackId, ct);
            if (track.RecordingMbid == Guid.Empty || track.SourceUrl?.StartsWith("https://www.youtube.com/", StringComparison.OrdinalIgnoreCase) == true)
            {
                skipped++;
                continue;
            }

            try
            {
                var fresh = await _metadata.GetRecordingAsync(track.RecordingMbid, ct);
                if (fresh is null || string.IsNullOrWhiteSpace(fresh.Genre) && fresh.MusicBrainzRating is null)
                {
                    skipped++;
                    continue;
                }
                var changed = !string.Equals(track.Genre, fresh.Genre, StringComparison.Ordinal)
                    || track.MusicBrainzRating != fresh.MusicBrainzRating;
                if (!changed) { unchanged++; continue; }
                if (!string.IsNullOrWhiteSpace(fresh.Genre)) track.Genre = fresh.Genre;
                if (fresh.MusicBrainzRating is not null) track.MusicBrainzRating = fresh.MusicBrainzRating;
                updated++;
                await _db.SaveChangesAsync(ct);
            }
            catch (MetadataUnavailableException) { failed++; }
            catch (HttpRequestException) { failed++; }
        }

        return new MetadataRefreshSummary(trackIds.Count, updated, unchanged, skipped, failed);
    }
}

public sealed record MetadataRefreshSummary(int Scanned, int Updated, int Unchanged, int Skipped, int Failed);
