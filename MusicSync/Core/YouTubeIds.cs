using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicSync.Core;

/// <summary>
/// Stable identifiers for YouTube-only tracks, which have no MusicBrainz recording ID.
/// </summary>
public static class YouTubeIds
{
    private static readonly Regex WatchUrl = new(
        "^https://www\\.youtube\\.com/watch\\?v=([A-Za-z0-9_-]{11})$",
        RegexOptions.CultureInvariant);

    public static bool TryGetVideoId(string url, out string videoId)
    {
        var match = WatchUrl.Match(url ?? string.Empty);
        videoId = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    public static Guid RecordingIdFor(string videoId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("musicsync:youtube:" + videoId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
