using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicSync.Infrastructure;

public sealed record YtDlpVideo(string VideoId, string Title, int? DurationMs);

/// <summary>
/// Parses yt-dlp "--flat-playlist --dump-json" output and validates YouTube video identifiers.
/// </summary>
public static class YtDlpSearchParser
{
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);
    private static readonly Regex WatchUrlPattern = new(
        "^https://www\\.youtube\\.com/watch\\?v=([A-Za-z0-9_-]{11})$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<YtDlpVideo> Parse(string jsonLines, int max)
    {
        var results = new List<YtDlpVideo>();
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (results.Count >= max)
                break;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                    continue;

                var id = idElement.GetString();
                if (id is null || !VideoIdPattern.IsMatch(id))
                    continue;

                var title = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;

                int? durationMs = null;
                if (root.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number)
                    durationMs = (int)Math.Round(durationElement.GetDouble() * 1000);

                results.Add(new YtDlpVideo(id, title, durationMs));
            }
            catch (JsonException)
            {
            }
        }

        return results;
    }

    public static string ToWatchUrl(string videoId)
    {
        if (!VideoIdPattern.IsMatch(videoId))
            throw new ArgumentException("El identificador de video no es válido.", nameof(videoId));

        return $"https://www.youtube.com/watch?v={videoId}";
    }

    public static bool IsWatchUrl(string url) => WatchUrlPattern.IsMatch(url);
}
