using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

public sealed class PlaylistImportService
{
    private readonly MusicSyncDbContext _db;
    private readonly IPlaylistSource _source;
    private readonly IMetadataProvider _metadata;
    private readonly IPopularityProvider _popularity;
    private readonly IMatchScorer _scorer;
    private readonly PlaylistOptions _options;

    public PlaylistImportService(
        MusicSyncDbContext db,
        IPlaylistSource source,
        IMetadataProvider metadata,
        IMatchScorer scorer,
        PlaylistOptions options,
        IPopularityProvider popularity)
    {
        _db = db;
        _source = source;
        _metadata = metadata;
        _popularity = popularity;
        _scorer = scorer;
        _options = options;
    }

    public async Task<PlaylistPreviewResult?> PreviewAlbumAsync(Guid releaseGroupMbid, CancellationToken ct)
    {
        var album = await _metadata.GetAlbumAsync(releaseGroupMbid, ct);
        if (album is null || album.Tracks.Count == 0)
            return null;

        var tracks = album.Tracks.Select(track => new ResolvedBulkTrack(
            $"album-{track.RecordingMbid:N}", track.Title, track.GetArtistNamesJoined(" & "), album.Title,
            track.DurationMs, new[] { new PlaylistCandidateDto(
                track.RecordingMbid, track.Title, GetArtistName(track), track.AlbumTitle ?? album.Title,
                track.ReleaseGroupMbid ?? releaseGroupMbid, track.DurationMs, 1, "album recording") }))
            .ToList();
        return await PersistBulkPreviewAsync($"album:{releaseGroupMbid:N}", album.Title, album.ArtistNames?.FirstOrDefault(), tracks, ct);
    }

    public async Task<PlaylistPreviewResult?> PreviewArtistAsync(
        Guid artistMbid,
        string artistName,
        int count,
        CancellationToken ct)
    {
        if (count <= 0 || count > _options.MaxTracks)
            throw new ArgumentException($"El límite de canciones es de 1 a {_options.MaxTracks}.", nameof(count));

        var top = await _popularity.TopRecordingsForArtistAsync(artistMbid, count, ct);
        var tracks = new List<ResolvedBulkTrack>();
        foreach (var recording in top)
        {
            var candidates = await _metadata.SearchTracksAsync(
                $"recording:\"{EscapeQuery(recording.Title)}\" AND artist:\"{EscapeQuery(artistName)}\"", ct);
            var match = candidates.FirstOrDefault(candidate => candidate.RecordingMbid == recording.RecordingMbid);
            var mapped = match is null
                ? Array.Empty<PlaylistCandidateDto>()
                : new[] { new PlaylistCandidateDto(
                    match.RecordingMbid, match.Title, GetArtistName(match), match.AlbumTitle,
                    match.ReleaseGroupMbid, match.DurationMs, 1, $"ListenBrainz position {recording.Position}") };
            tracks.Add(new ResolvedBulkTrack(
                $"artist-{recording.RecordingMbid:N}", recording.Title, artistName, null, 0, mapped));
        }

        return await PersistBulkPreviewAsync($"artist:{artistMbid:N}", artistName, artistName, tracks, ct);
    }

    public async Task<PlaylistPreviewResult> PreviewAsync(string url, CancellationToken ct)
    {
        var playlist = await _source.GetPlaylistAsync(url, ct);
        if (playlist.Tracks.Count == 0)
            throw new InvalidOperationException("La lista de Spotify no contiene canciones públicas.");
        if (playlist.Tracks.Count > _options.MaxTracks)
            throw new InvalidOperationException($"La lista supera el límite de {_options.MaxTracks} canciones.");

        var existing = await _db.PlaylistImports
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.ExternalId == playlist.Id, ct);
        if (existing is not null)
            return ToPreview(existing);

        var import = new PlaylistImport
        {
            ExternalId = playlist.Id,
            SourceUrl = url,
            Name = playlist.Name,
            OwnerName = playlist.OwnerDisplayName,
            TotalTracks = playlist.TotalTracks,
            Status = PlaylistImportStatus.Previewed
        };

        for (var position = 0; position < playlist.Tracks.Count; position++)
        {
            ct.ThrowIfCancellationRequested();
            var sourceTrack = playlist.Tracks[position];
            var target = new TrackInfo
            {
                Title = sourceTrack.Name,
                ArtistName = sourceTrack.ArtistName,
                AlbumTitle = sourceTrack.AlbumName,
                DurationMs = sourceTrack.DurationMs
            };

            var query = $"recording:\"{EscapeQuery(sourceTrack.Name)}\"";
            if (!string.IsNullOrWhiteSpace(sourceTrack.ArtistName))
                query += $" AND artist:\"{EscapeQuery(sourceTrack.ArtistName)}\"";

            var candidates = await _metadata.SearchTracksAsync(query, ct);
            var ranked = candidates
                .Select(candidate =>
                {
                    var score = _scorer.Score(target, new SourceCandidate
                    {
                        Title = candidate.Title,
                        ArtistName = GetArtistName(candidate),
                        DurationMs = candidate.DurationMs > 0 ? candidate.DurationMs : null,
                        SourceUrl = $"musicbrainz://recording/{candidate.RecordingMbid}"
                    });
                    return new PlaylistMatchCandidate(candidate, score);
                })
                .OrderByDescending(item => item.Score.Score)
                .Take(_options.MaxCandidates)
                .ToList();

            var best = ranked.FirstOrDefault();
            var status = best is null || (!best.Score.NeedsReview && !best.Score.AutoAccept)
                ? PlaylistImportItemStatus.NoMatch
                : best.Score.AutoAccept
                    ? PlaylistImportItemStatus.Matched
                    : PlaylistImportItemStatus.NeedsReview;

            import.Items.Add(new PlaylistImportItem
            {
                Position = position + 1,
                ExternalTrackId = sourceTrack.Id,
                Title = sourceTrack.Name,
                ArtistName = sourceTrack.ArtistName ?? string.Empty,
                AlbumName = sourceTrack.AlbumName,
                DurationMs = sourceTrack.DurationMs,
                Status = status,
                SelectedRecordingMbid = status == PlaylistImportItemStatus.Matched ? best!.Track.RecordingMbid : null,
                CandidatesJson = JsonSerializer.Serialize(ranked.Select(item => new PlaylistCandidateDto(
                    item.Track.RecordingMbid,
                    item.Track.Title,
                    GetArtistName(item.Track),
                    item.Track.AlbumTitle,
                    item.Track.ReleaseGroupMbid,
                    item.Track.DurationMs,
                    item.Score.Score,
                    item.Score.Reason)))
            });
        }

        _db.PlaylistImports.Add(import);
        await _db.SaveChangesAsync(ct);
        return ToPreview(import);
    }

    public async Task<PlaylistEnqueueResult?> EnqueueAsync(
        int importId,
        IReadOnlyCollection<int> itemIds,
        IReadOnlyDictionary<int, Guid>? selections,
        CancellationToken ct)
    {
        var import = await _db.PlaylistImports
            .Include(item => item.Items.OrderBy(child => child.Position))
            .SingleOrDefaultAsync(item => item.Id == importId, ct);
        if (import is null)
            return null;

        var requested = itemIds.Count == 0
            ? import.Items.Where(item => item.Status == PlaylistImportItemStatus.Matched).ToList()
            : import.Items.Where(item => itemIds.Contains(item.Id)).ToList();
        var created = new List<PlaylistEnqueuedItem>();
        var skipped = new List<PlaylistSkippedItem>();

        foreach (var item in requested.OrderBy(item => item.Position))
        {
            if (item.DownloadJobId is not null)
            {
                skipped.Add(new(item.Id, item.Position, "AlreadyEnqueued"));
                continue;
            }

            var selected = selections is not null && selections.TryGetValue(item.Id, out var requestedMbid)
                ? requestedMbid
                : item.SelectedRecordingMbid;
            if (selected is null)
            {
                skipped.Add(new(item.Id, item.Position, "NoConfirmedMatch"));
                continue;
            }

            var candidates = JsonSerializer.Deserialize<List<PlaylistCandidateDto>>(item.CandidatesJson) ?? new();
            var candidate = candidates.SingleOrDefault(value => value.RecordingMbid == selected.Value);
            if (candidate is null)
            {
                skipped.Add(new(item.Id, item.Position, "CandidateNotFound"));
                continue;
            }

            var track = await TrackCatalog.GetOrCreateTrackAsync(_db, new TrackInfo
            {
                RecordingMbid = candidate.RecordingMbid,
                Title = candidate.Title,
                ArtistName = candidate.ArtistName,
                AlbumTitle = candidate.AlbumTitle,
                ReleaseGroupMbid = candidate.ReleaseGroupMbid,
                DurationMs = candidate.DurationMs
            }, ct);

            var active = await _db.DownloadJobs.AnyAsync(job =>
                job.TrackId == track.Id && DownloadJobStatus.Active.Contains(job.Status), ct);
            if (active)
            {
                item.Status = PlaylistImportItemStatus.Duplicate;
                skipped.Add(new(item.Id, item.Position, "ActiveJob"));
                continue;
            }

            var job = new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Queued };
            _db.DownloadJobs.Add(job);
            await _db.SaveChangesAsync(ct);
            item.DownloadJobId = job.Id;
            item.TrackId = track.Id;
            item.SelectedRecordingMbid = candidate.RecordingMbid;
            item.Status = PlaylistImportItemStatus.Enqueued;
            created.Add(new(item.Id, item.Position, job.Id));
        }

        import.Status = import.Items.All(item => item.Status == PlaylistImportItemStatus.Enqueued)
            ? PlaylistImportStatus.Enqueued
            : PlaylistImportStatus.Previewed;
        import.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new PlaylistEnqueueResult(import.Id, created, skipped);
    }

    public async Task<PlaylistPreviewResult?> GetAsync(int importId, CancellationToken ct)
    {
        var import = await _db.PlaylistImports.Include(item => item.Items)
            .AsNoTracking().SingleOrDefaultAsync(item => item.Id == importId, ct);
        return import is null ? null : ToPreview(import);
    }

    private async Task<PlaylistPreviewResult> PersistBulkPreviewAsync(
        string externalId,
        string name,
        string? owner,
        IReadOnlyList<ResolvedBulkTrack> tracks,
        CancellationToken ct)
    {
        var existing = await _db.PlaylistImports.Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.ExternalId == externalId, ct);
        if (existing is not null)
            return ToPreview(existing);

        var import = new PlaylistImport
        {
            ExternalId = externalId,
            SourceUrl = externalId,
            Name = name,
            OwnerName = owner,
            TotalTracks = tracks.Count,
            Status = PlaylistImportStatus.Previewed
        };
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var best = track.Candidates.FirstOrDefault();
            import.Items.Add(new PlaylistImportItem
            {
                Position = index + 1,
                ExternalTrackId = track.ExternalId,
                Title = track.Title,
                ArtistName = track.ArtistName,
                AlbumName = track.AlbumName,
                DurationMs = track.DurationMs,
                Status = best is null ? PlaylistImportItemStatus.NoMatch : PlaylistImportItemStatus.Matched,
                SelectedRecordingMbid = best?.RecordingMbid,
                CandidatesJson = JsonSerializer.Serialize(track.Candidates)
            });
        }
        _db.PlaylistImports.Add(import);
        await _db.SaveChangesAsync(ct);
        return ToPreview(import);
    }

    private PlaylistPreviewResult ToPreview(PlaylistImport import) => new(
        import.Id,
        import.ExternalId,
        import.Name,
        import.OwnerName,
        import.Status,
        import.Items.OrderBy(item => item.Position).Select(item => new PlaylistPreviewItem(
            item.Id, item.Position, item.Title, item.ArtistName, item.AlbumName, item.DurationMs,
            item.Status, item.SelectedRecordingMbid,
            JsonSerializer.Deserialize<List<PlaylistCandidateDto>>(item.CandidatesJson) ?? new())).ToList());

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string GetArtistName(TrackInfo track) =>
        track.Artists.Count > 0 ? track.GetArtistNamesJoined(" & ") : track.ArtistName ?? string.Empty;

    private sealed record PlaylistMatchCandidate(TrackInfo Track, ScoreResult Score);
    private sealed record ResolvedBulkTrack(
        string ExternalId, string Title, string ArtistName, string? AlbumName, int DurationMs,
        IReadOnlyList<PlaylistCandidateDto> Candidates);
}

public sealed record PlaylistCandidateDto(
    Guid RecordingMbid,
    string Title,
    string ArtistName,
    string? AlbumTitle,
    Guid? ReleaseGroupMbid,
    int DurationMs,
    double Score,
    string Reason);

public sealed record PlaylistPreviewResult(
    int ImportId,
    string ExternalId,
    string Name,
    string? OwnerName,
    string Status,
    IReadOnlyList<PlaylistPreviewItem> Items);

public sealed record PlaylistPreviewItem(
    int ItemId,
    int Position,
    string Title,
    string ArtistName,
    string? AlbumName,
    int DurationMs,
    string Status,
    Guid? SelectedRecordingMbid,
    IReadOnlyList<PlaylistCandidateDto> Candidates);

public sealed record PlaylistEnqueueResult(
    int ImportId,
    IReadOnlyList<PlaylistEnqueuedItem> Enqueued,
    IReadOnlyList<PlaylistSkippedItem> Skipped);

public sealed record PlaylistEnqueuedItem(int ItemId, int Position, int JobId);
public sealed record PlaylistSkippedItem(int ItemId, int Position, string Reason);
