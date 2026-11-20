namespace MusicSync.Core;

public sealed record YouTubeVideoMetadata(
    string VideoId,
    string Url,
    string RawTitle,
    string? Uploader,
    int? DurationMs);

public interface IYouTubeMetadataProvider
{
    Task<YouTubeVideoMetadata> GetMetadataAsync(string url, CancellationToken ct);
}
