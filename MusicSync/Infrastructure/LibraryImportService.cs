using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

/// <summary>
/// Imports files that already exist in the library folder. Files are matched to MusicBrainz and
/// recorded in the database only after the user picks a candidate. Nothing is downloaded or rewritten.
/// </summary>
public sealed class LibraryImportService
{
    public const int MaxCandidates = 10;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav"
    };

    private static readonly Regex TrackNumberPrefix = new(@"^\d{1,3}\s*-\s*", RegexOptions.CultureInvariant);

    private readonly MusicSyncDbContext _db;
    private readonly IMetadataProvider _metadata;
    private readonly IVideoSearchProvider _video;
    private readonly IMatchScorer _scorer;
    private readonly FfmpegAudioTranscoder _transcoder;
    private readonly LibraryOptions _library;
    private readonly ICoverArtProvider _coverArt;
    private readonly ITagWriter _tagWriter;
    private readonly ILogger<LibraryImportService>? _logger;

    public LibraryImportService(
        MusicSyncDbContext db,
        IMetadataProvider metadata,
        IVideoSearchProvider video,
        IMatchScorer scorer,
        FfmpegAudioTranscoder transcoder,
        LibraryOptions library,
        ICoverArtProvider coverArt,
        ITagWriter tagWriter,
        ILogger<LibraryImportService>? logger = null)
    {
        _db = db;
        _metadata = metadata;
        _video = video;
        _scorer = scorer;
        _transcoder = transcoder;
        _library = library;
        _coverArt = coverArt;
        _tagWriter = tagWriter;
        _logger = logger;
    }

    public async Task<ScanSummary> ScanAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(_library.Root);
        if (!Directory.Exists(root))
            throw new InvalidOperationException("La carpeta de la biblioteca no existe.");

        var known = new HashSet<string>(StringComparer.Ordinal);
        known.UnionWith(await _db.LibraryImports.Select(item => item.RelativePath).ToListAsync(ct));
        known.UnionWith(await _db.LibraryFiles.Select(item => item.RelativePath).ToListAsync(ct));

        var added = 0;
        var alreadyKnown = 0;
        var unrecognized = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!AudioExtensions.Contains(Path.GetExtension(file)))
                continue;

            var relative = LibraryPathGuard.NormalizeRelative(root, file);
            if (known.Contains(relative))
            {
                alreadyKnown++;
                continue;
            }

            var parts = relative.Split('/');
            string artist;
            string? album = null;
            string title;
            if (parts.Length == 2)
            {
                artist = parts[0];
                title = Path.GetFileNameWithoutExtension(parts[1]);
            }
            else if (parts.Length == 3)
            {
                artist = parts[0];
                album = parts[1];
                title = TrackNumberPrefix.Replace(Path.GetFileNameWithoutExtension(parts[2]), string.Empty);
            }
            else
            {
                unrecognized++;
                continue;
            }

            _db.LibraryImports.Add(new LibraryImport
            {
                RelativePath = relative,
                ArtistName = artist,
                AlbumTitle = album,
                Title = title,
                Status = LibraryImportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            known.Add(relative);
            added++;
        }

        await _db.SaveChangesAsync(ct);
        return new ScanSummary(added, alreadyKnown, unrecognized);
    }

    /// <summary>
    /// Searches MusicBrainz for each pending or previously identified item. When no candidate
    /// matches the file's artist, searches YouTube as a fallback. Throws
    /// <see cref="MetadataUnavailableException"/> if MusicBrainz stays unavailable; progress so far is kept.
    /// </summary>
    public async Task<int> IdentifyAsync(IReadOnlyCollection<int>? importIds, CancellationToken ct)
    {
        var query = _db.LibraryImports
            .Include(item => item.Candidates)
            .Where(item => item.Status != LibraryImportStatus.Imported && item.Status != LibraryImportStatus.Rejected);

        if (importIds is not null)
            query = query.Where(item => importIds.Contains(item.Id));
        else
            query = query.Where(item => item.Status == LibraryImportStatus.Pending);

        var imports = await query.OrderBy(item => item.Id).ToListAsync(ct);
        var identified = 0;

        foreach (var import in imports)
        {
            ct.ThrowIfCancellationRequested();
            var absolutePath = LibraryPathGuard.ResolveRelativePath(_library.Root, import.RelativePath);
            if (!File.Exists(absolutePath))
            {
                import.Error = "El archivo ya no existe en la carpeta de la biblioteca.";
                await _db.SaveChangesAsync(ct);
                continue;
            }

            var duration = await _transcoder.ProbeDurationMsAsync(absolutePath, ct) ?? 0;
            var target = new TrackInfo
            {
                RecordingMbid = Guid.Empty,
                Title = import.Title,
                ArtistName = import.ArtistName,
                AlbumTitle = import.AlbumTitle,
                DurationMs = duration
            };

            var results = await _metadata.SearchTracksAsync($"{import.Title} - {import.ArtistName}", ct);
            var ranked = results
                .Select(track => new RankedCandidate(
                    LibraryImportProvider.MusicBrainz,
                    track.RecordingMbid,
                    Limit(track.Title, 500),
                    track.Artists,
                    Limit(track.AlbumTitle, 500),
                    track.ReleaseGroupMbid,
                    track.Year,
                    track.DurationMs > 0 ? track.DurationMs : null,
                    track.Isrc,
                    $"musicbrainz://recording/{track.RecordingMbid}",
                    _scorer.Score(target, ToSourceCandidate(track))))
                .ToList();

            string? error = null;
            if (!ranked.Any(item => IsAccepted(item.Result)) && _library.YouTubeFallback)
            {
                try
                {
                    var videos = await _video.SearchAsync(target, _library.YouTubeMaxCandidates, ct);
                    foreach (var video in videos)
                    {
                        if (!YouTubeIds.TryGetVideoId(video.SourceUrl, out _))
                            continue;

                        var artists = string.IsNullOrWhiteSpace(video.ArtistName)
                            ? Array.Empty<ArtistCredit>()
                            : new[] { new ArtistCredit(video.ArtistName, Guid.Empty) };

                        ranked.Add(new RankedCandidate(
                            LibraryImportProvider.YouTube,
                            Guid.Empty,
                            Limit(string.IsNullOrWhiteSpace(video.Title) ? import.Title : video.Title, 500),
                            artists,
                            Limit(import.AlbumTitle, 500),
                            null,
                            null,
                            video.DurationMs,
                            null,
                            video.SourceUrl,
                            _scorer.Score(target, video)));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = Limit($"YouTube: {ex.Message}", 500);
                }
            }

            ranked = ranked
                .OrderByDescending(item => item.Result.Score)
                .Take(MaxCandidates)
                .ToList();

            _db.LibraryImportCandidates.RemoveRange(import.Candidates);
            import.Candidates.Clear();

            var rank = 0;
            foreach (var item in ranked)
            {
                rank++;
                import.Candidates.Add(new LibraryImportCandidate
                {
                    Rank = rank,
                    RecordingMbid = item.RecordingMbid,
                    Title = item.Title,
                    ArtistsJson = JsonSerializer.Serialize(item.Artists),
                    AlbumTitle = item.AlbumTitle,
                    ReleaseGroupMbid = item.ReleaseGroupMbid,
                    Year = item.Year,
                    DurationMs = item.DurationMs,
                    Isrc = item.Isrc,
                    Score = (float)item.Result.Score,
                    Reason = Limit(item.Result.Reason, 500),
                    Provider = item.Provider,
                    SourceUrl = Limit(item.SourceUrl, 500)
                });
            }

            import.Status = ranked.Any(item => IsAccepted(item.Result))
                ? LibraryImportStatus.Identified
                : LibraryImportStatus.NoMatch;
            import.IdentifiedAt = DateTime.UtcNow;
            import.Error = error;
            await _db.SaveChangesAsync(ct);
            identified++;
        }

        return identified;
    }

    private static bool IsAccepted(ScoreResult result) =>
        (result.NeedsReview || result.AutoAccept) && result.ArtistMatched;

    public async Task<AcceptOutcome> AcceptCandidateAsync(int importId, int candidateId, CancellationToken ct)
    {
        var import = await _db.LibraryImports
            .Include(item => item.Candidates)
            .SingleOrDefaultAsync(item => item.Id == importId, ct);
        if (import is null)
            return AcceptOutcome.ImportNotFound;

        if (import.Status == LibraryImportStatus.Imported)
            return AcceptOutcome.AlreadyImported;

        var candidate = import.Candidates.FirstOrDefault(item => item.Id == candidateId);
        if (candidate is null)
            return AcceptOutcome.CandidateNotFound;

        var absolutePath = LibraryPathGuard.ResolveRelativePath(_library.Root, import.RelativePath);
        if (!File.Exists(absolutePath))
            return AcceptOutcome.FileMissing;

        var isYouTube = string.Equals(candidate.Provider, LibraryImportProvider.YouTube, StringComparison.Ordinal);
        var recordingId = candidate.RecordingMbid;
        if (isYouTube)
        {
            if (!YouTubeIds.TryGetVideoId(candidate.SourceUrl, out var videoId))
                return AcceptOutcome.CandidateNotFound;

            recordingId = YouTubeIds.RecordingIdFor(videoId);
        }

        var artists = JsonSerializer.Deserialize<List<ArtistCredit>>(candidate.ArtistsJson) ?? new List<ArtistCredit>();
        var title = candidate.Title;
        if (isYouTube && artists.Count == 0)
        {
            // The video title had no "Artist - Title" separator, so keep the folder values.
            title = import.Title;
            if (!string.IsNullOrWhiteSpace(import.ArtistName))
                artists.Add(new ArtistCredit(import.ArtistName, Guid.Empty));
        }

        var track = await TrackCatalog.GetOrCreateTrackAsync(_db, new TrackInfo
        {
            RecordingMbid = recordingId,
            Title = title,
            ArtistName = artists.FirstOrDefault()?.Name,
            Artists = artists,
            AlbumTitle = candidate.AlbumTitle,
            ReleaseGroupMbid = candidate.ReleaseGroupMbid,
            Year = candidate.Year,
            Isrc = candidate.Isrc,
            DurationMs = candidate.DurationMs ?? 0
        }, ct);

        if (isYouTube && string.IsNullOrWhiteSpace(track.SourceUrl))
            track.SourceUrl = candidate.SourceUrl;

        if (await _db.LibraryFiles.AnyAsync(item => item.TrackId == track.Id, ct))
            return AcceptOutcome.RecordingAlreadyInLibrary;

        var existingHash = await ComputeSha256Async(absolutePath, ct);
        if (await _db.LibraryFiles.AnyAsync(item => item.Sha256 == existingHash, ct))
            return AcceptOutcome.FileAlreadyInLibrary;

        await TryEmbedCoverAsync(absolutePath, candidate, ct);

        _db.LibraryFiles.Add(new LibraryFile
        {
            TrackId = track.Id,
            RelativePath = import.RelativePath,
            Format = Path.GetExtension(import.RelativePath).TrimStart('.').ToLowerInvariant(),
            SizeBytes = new FileInfo(absolutePath).Length,
            Sha256 = await ComputeSha256Async(absolutePath, ct),
            AddedAt = DateTime.UtcNow
        });

        import.Status = LibraryImportStatus.Imported;
        import.TrackId = track.Id;
        import.SourceUrl = candidate.SourceUrl;
        import.Error = null;
        await _db.SaveChangesAsync(ct);
        return AcceptOutcome.Imported;
    }

    /// <summary>
    /// Backfills front cover art for files that were imported before cover embedding was enabled.
    /// Only imported files whose track has a MusicBrainz release group are considered. Each modified
    /// file gets its stored hash and size refreshed, and one bad file never stops the batch.
    /// </summary>
    public async Task<CoverBackfillSummary> BackfillCoversAsync(
        IReadOnlyCollection<int>? importIds,
        CancellationToken ct)
    {
        var query = _db.LibraryImports
            .Where(item => item.Status == LibraryImportStatus.Imported && item.TrackId != null);

        if (importIds is not null)
            query = query.Where(item => importIds.Contains(item.Id));

        var imports = await query.OrderBy(item => item.Id).ToListAsync(ct);
        var embedded = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var import in imports)
        {
            ct.ThrowIfCancellationRequested();

            var track = await _db.Tracks.SingleOrDefaultAsync(item => item.Id == import.TrackId, ct);
            if (track?.ReleaseGroupMbid is not Guid releaseGroup || releaseGroup == Guid.Empty)
            {
                skipped++;
                continue;
            }

            string absolutePath;
            try
            {
                absolutePath = LibraryPathGuard.ResolveRelativePath(_library.Root, import.RelativePath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _logger?.LogWarning(ex, "Skipped cover backfill for unsafe library path {RelativePath}.", import.RelativePath);
                failed++;
                continue;
            }

            if (!File.Exists(absolutePath))
            {
                _logger?.LogWarning("Skipped cover backfill for missing library file {RelativePath}.", import.RelativePath);
                failed++;
                continue;
            }

            try
            {
                var cover = await _coverArt.GetFrontCoverAsync(releaseGroup, ct);
                if (cover is not { Length: > 0 })
                {
                    skipped++;
                    continue;
                }

                await _tagWriter.WriteCoverAsync(absolutePath, cover, ct);

                var libraryFile = await _db.LibraryFiles.SingleOrDefaultAsync(
                    item => item.TrackId == track.Id && item.RelativePath == import.RelativePath,
                    ct);
                if (libraryFile is not null)
                {
                    libraryFile.SizeBytes = new FileInfo(absolutePath).Length;
                    libraryFile.Sha256 = await ComputeSha256Async(absolutePath, ct);
                    await _db.SaveChangesAsync(ct);
                }

                embedded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not backfill cover art for imported file {RelativePath}.", import.RelativePath);
                failed++;
            }
        }

        return new CoverBackfillSummary(imports.Count, embedded, skipped, failed);
    }

    public async Task<AcceptOutcome> RejectAsync(int importId, CancellationToken ct)
    {
        var import = await _db.LibraryImports.SingleOrDefaultAsync(item => item.Id == importId, ct);
        if (import is null)
            return AcceptOutcome.ImportNotFound;

        if (import.Status == LibraryImportStatus.Imported)
            return AcceptOutcome.AlreadyImported;

        import.Status = LibraryImportStatus.Rejected;
        await _db.SaveChangesAsync(ct);
        return AcceptOutcome.Rejected;
    }

    /// <summary>
    /// Changes the stored path of a file. The file itself is not moved. The new path must stay
    /// inside the library and point to an existing file.
    /// </summary>
    public async Task<AcceptOutcome> UpdatePathAsync(int importId, string relativePath, CancellationToken ct)
    {
        var import = await _db.LibraryImports.SingleOrDefaultAsync(item => item.Id == importId, ct);
        if (import is null)
            return AcceptOutcome.ImportNotFound;

        var newRelative = LibraryPathGuard.NormalizeRelative(_library.Root, LibraryPathGuard.ResolveRelativePath(_library.Root, relativePath));
        if (!File.Exists(LibraryPathGuard.ResolveRelativePath(_library.Root, newRelative)))
            return AcceptOutcome.FileMissing;

        if (newRelative == import.RelativePath)
            return AcceptOutcome.Updated;

        var conflict = await _db.LibraryImports.AnyAsync(item => item.RelativePath == newRelative && item.Id != importId, ct)
            || await _db.LibraryFiles.AnyAsync(item => item.RelativePath == newRelative, ct);
        if (conflict)
            return AcceptOutcome.PathInUse;

        var oldRelative = import.RelativePath;
        import.RelativePath = newRelative;
        if (import.TrackId is int trackId)
        {
            var file = await _db.LibraryFiles.SingleOrDefaultAsync(
                item => item.TrackId == trackId && item.RelativePath == oldRelative, ct);
            if (file is not null)
                file.RelativePath = newRelative;
        }

        await _db.SaveChangesAsync(ct);
        return AcceptOutcome.Updated;
    }

    private async Task TryEmbedCoverAsync(string filePath, LibraryImportCandidate candidate, CancellationToken ct)
    {
        if (!_library.EmbedCoverOnImport || candidate.ReleaseGroupMbid is not Guid releaseGroup || releaseGroup == Guid.Empty)
            return;

        try
        {
            var cover = await _coverArt.GetFrontCoverAsync(releaseGroup, ct);
            if (cover is not { Length: > 0 })
                return;

            await _tagWriter.WriteCoverAsync(filePath, cover, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cover art is optional: an unsupported format or a broken image must not block the import.
            _logger?.LogWarning(ex, "Could not embed cover art into imported file {FilePath}.", filePath);
        }
    }

    private static SourceCandidate ToSourceCandidate(TrackInfo track) => new()
    {
        Title = track.Title,
        ArtistName = track.GetArtistNamesJoined(" & "),
        SourceUrl = $"musicbrainz://recording/{track.RecordingMbid}",
        Format = "musicbrainz",
        DurationMs = track.DurationMs > 0 ? track.DurationMs : null
    };

    private sealed record RankedCandidate(
        string Provider,
        Guid RecordingMbid,
        string Title,
        IReadOnlyList<ArtistCredit> Artists,
        string? AlbumTitle,
        Guid? ReleaseGroupMbid,
        int? Year,
        int? DurationMs,
        string? Isrc,
        string SourceUrl,
        ScoreResult Result);

    private static string Limit(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}

public sealed record ScanSummary(int Added, int AlreadyKnown, int Unrecognized);

public sealed record CoverBackfillSummary(int Scanned, int Embedded, int Skipped, int Failed);

public enum AcceptOutcome
{
    Imported,
    Rejected,
    Updated,
    ImportNotFound,
    CandidateNotFound,
    AlreadyImported,
    RecordingAlreadyInLibrary,
    FileAlreadyInLibrary,
    FileMissing,
    PathInUse
}
