using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

public sealed class YouTubeLinkService
{
    private readonly IYouTubeMetadataProvider _youtube;
    private readonly IMetadataProvider _metadata;
    private readonly IMatchScorer _scorer;
    private readonly MusicSyncDbContext _db;

    public YouTubeLinkService(IYouTubeMetadataProvider youtube, IMetadataProvider metadata, IMatchScorer scorer, MusicSyncDbContext db)
    {
        _youtube = youtube; _metadata = metadata; _scorer = scorer; _db = db;
    }

    public async Task<YouTubePreview> PreviewAsync(string url, CancellationToken ct)
    {
        var video = await _youtube.GetMetadataAsync(url, ct);
        var parsed = VideoTitleParser.Parse(video.RawTitle);
        var artist = parsed.Artist ?? video.Uploader ?? string.Empty;
        var query = $"recording:\"{EscapeQuery(parsed.Title)}\"";
        if (!string.IsNullOrWhiteSpace(artist)) query += $" AND artist:\"{EscapeQuery(artist)}\"";
        var target = new TrackInfo { Title = parsed.Title, ArtistName = artist, DurationMs = video.DurationMs ?? 0 };
        var ranked = (await _metadata.SearchTracksAsync(query, ct))
            .Select(track => new { Track = track, Score = _scorer.Score(target, new SourceCandidate
            {
                Title = track.Title, ArtistName = GetArtistName(track), DurationMs = track.DurationMs > 0 ? track.DurationMs : null,
                SourceUrl = $"musicbrainz://recording/{track.RecordingMbid}"
            })})
            .OrderByDescending(item => item.Score.Score).Take(10)
            .Select(item => new YouTubeCandidateDto(item.Track.RecordingMbid, item.Track.Title, GetArtistName(item.Track), item.Track.AlbumTitle,
                item.Track.ReleaseGroupMbid, item.Track.DurationMs, item.Score.Score, item.Score.Reason, item.Score.AutoAccept || item.Score.NeedsReview))
            .ToList();
        return new YouTubePreview(video.VideoId, video.Url, video.RawTitle, parsed.Title, artist, video.Uploader, video.DurationMs, ranked);
    }

    public async Task<YouTubeEnqueueResult> EnqueueAsync(string url, Guid recordingMbid, CancellationToken ct)
    {
        var preview = await PreviewAsync(url, ct);
        var candidate = preview.Candidates.SingleOrDefault(item => item.RecordingMbid == recordingMbid)
            ?? throw new InvalidOperationException("La grabación indicada no pertenece a los candidatos del video.");
        var track = await TrackCatalog.GetOrCreateTrackAsync(_db, new TrackInfo
        {
            RecordingMbid = candidate.RecordingMbid, Title = candidate.Title, ArtistName = candidate.ArtistName,
            AlbumTitle = candidate.AlbumTitle, ReleaseGroupMbid = candidate.ReleaseGroupMbid, DurationMs = candidate.DurationMs
        }, ct);
        if (await _db.DownloadJobs.AnyAsync(job => job.TrackId == track.Id && DownloadJobStatus.Active.Contains(job.Status), ct))
            throw new InvalidOperationException("Esta grabación ya tiene un trabajo activo.");
        var job = new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.Approved, ProviderName = "yt-dlp", SourceUrl = preview.SourceUrl, MatchScore = (float)candidate.Score };
        _db.DownloadJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return new YouTubeEnqueueResult(job.Id, job.Status);
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string GetArtistName(TrackInfo track) => track.Artists.Count > 0 ? track.GetArtistNamesJoined(" & ") : track.ArtistName ?? string.Empty;
}

public sealed record YouTubePreview(string VideoId, string SourceUrl, string RawTitle, string Title, string ArtistName, string? Uploader, int? DurationMs, IReadOnlyList<YouTubeCandidateDto> Candidates);
public sealed record YouTubeCandidateDto(Guid RecordingMbid, string Title, string ArtistName, string? AlbumTitle, Guid? ReleaseGroupMbid, int DurationMs, double Score, string Reason, bool Confident);
public sealed record YouTubeEnqueueResult(int JobId, string JobStatus);
