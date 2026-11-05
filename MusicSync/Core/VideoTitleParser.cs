using System.Text.RegularExpressions;

namespace MusicSync.Core;

/// <summary>
/// Reads "Artist - Title" from a video title and drops common upload noise such as
/// "(Official Audio)". Used only for library import comparisons; the raw title is kept otherwise.
/// </summary>
public static class VideoTitleParser
{
    private static readonly Regex Separator = new(
        @"^(?<artist>.+?)\s[-–—]\s(?<title>.+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NoiseSuffix = new(
        @"\s*[\(\[]\s*(official\s*(music\s*)?(video|audio)|music\s*video|lyric[s]?\s*video|lyrics?|audio|video|visuali[sz]er|hd|hq|4k|remaster(ed)?(\s*\d{4})?)\s*[\)\]]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ParsedVideoTitle Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        while (NoiseSuffix.Match(text) is { Success: true } noise)
            text = text[..noise.Index].Trim();

        var match = Separator.Match(text);
        if (!match.Success)
            return new ParsedVideoTitle(null, text);

        var artist = match.Groups["artist"].Value.Trim();
        var title = match.Groups["title"].Value.Trim();
        return artist.Length == 0 || title.Length == 0
            ? new ParsedVideoTitle(null, text)
            : new ParsedVideoTitle(artist, title);
    }
}

/// <summary>Artist and title read from a video title. Artist is null when the title has no separator.</summary>
public sealed record ParsedVideoTitle(string? Artist, string Title);
