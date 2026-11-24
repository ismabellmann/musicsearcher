using System.Net.Http;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

namespace MusicSync.Infrastructure;

/// <summary>
/// MusicBrainz canonical metadata client.
/// Server-side client with global 1 rps rate limiter, proper User-Agent header,
/// and caching as specified in sections 8.2 and 9 of PROJECT_CONTEXT.md.
/// </summary>
public class MusicBrainzClient : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzClient> _logger;
    private readonly MusicBrainzRequestGate _gate;

    // In-memory cache for metadata (rarely changes, TTL of days)
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    public MusicBrainzClient(
        HttpClient httpClient,
        ILogger<MusicBrainzClient> logger,
        MusicBrainzOptions? options = null,
        MusicBrainzRequestGate? gate = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _gate = gate ?? new MusicBrainzRequestGate();

        // Configure HttpClient for MusicBrainz
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd((options ?? new MusicBrainzOptions()).UserAgent);
    }

    /// <summary>
    /// Search for tracks matching the given query.
    /// Supports artist:name style narrowing per section 8.1.
    /// </summary>
    public async Task<IReadOnlyList<TrackInfo>> SearchTracksAsync(string query, CancellationToken ct)
    {
        var cacheKey = "search-tracks:" + NormalizeQuery(query);
        if (TryGetCached(cacheKey, out var cachedResult))
        {
            _logger.LogDebug("Cache hit for track search: {Query}", query);
            return ParseTrackSearchResponse(cachedResult);
        }

        _logger.LogInformation("Searching MusicBrainz tracks: {Query}", query);

        var content = await GetContentAsync(BuildSearchUri("recording", query), "track search", notFoundIsNull: false, ct) ?? string.Empty;
        var results = ParseTrackSearchResponse(content);

        // Cache the result
        PutCached(cacheKey, content);

        return results;
    }

    /// <summary>
    /// Search for albums matching the given query.
    /// </summary>
    public async Task<IReadOnlyList<TrackInfo>> SearchAlbumsAsync(string query, CancellationToken ct)
    {
        var cacheKey = "search-albums:" + NormalizeQuery(query);
        if (TryGetCached(cacheKey, out var cachedResult))
            return ParseAlbumSearchResponse(cachedResult);

        _logger.LogInformation("Searching MusicBrainz albums: {Query}", query);

        var content = await GetContentAsync(BuildSearchUri("release", query), "album search", notFoundIsNull: false, ct) ?? string.Empty;
        var results = ParseAlbumSearchResponse(content);

        // Cache the result
        PutCached(cacheKey, content);

        return results;
    }

    /// <summary>
    /// Search for artists matching the given query.
    /// </summary>
    public async Task<IReadOnlyList<TrackInfo>> SearchArtistsAsync(string query, CancellationToken ct)
    {
        var cacheKey = "search-artists:" + NormalizeQuery(query);
        if (TryGetCached(cacheKey, out var cachedResult))
            return ParseArtistSearchResponse(cachedResult);

        _logger.LogInformation("Searching MusicBrainz artists: {Query}", query);

        var content = await GetContentAsync(BuildSearchUri("artist", query), "artist search", notFoundIsNull: false, ct) ?? string.Empty;
        var results = ParseArtistSearchResponse(content);

        // Cache the result
        PutCached(cacheKey, content);

        return results;
    }

    /// <summary>
    /// Get detailed album information by release group MBID.
    /// </summary>
    public async Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct)
    {
        var cacheKey = "album-detail:" + releaseGroupMbid.ToString("D");
        if (TryGetCached(cacheKey, out var cachedAlbum))
        {
            _logger.LogDebug("Cache hit for album detail: {Mbid}", releaseGroupMbid);
            return ParseAlbumDetailResponse(cachedAlbum);
        }

        _logger.LogInformation("Fetching MusicBrainz album detail: {Mbid}", releaseGroupMbid);

        var releaseGroupContent = await GetContentAsync(BuildReleaseGroupUri(releaseGroupMbid), "album release lookup", notFoundIsNull: true, ct);
        if (releaseGroupContent is null)
            return null;

        var releaseId = ParseFirstReleaseId(releaseGroupContent);
        if (releaseId == Guid.Empty)
            return null;

        var content = await GetContentAsync(BuildReleaseUri(releaseId), "album detail", notFoundIsNull: true, ct);
        if (content is null)
            return null;

        var album = ParseAlbumDetailResponse(content);

        // Cache the result for 7 days
        PutCached(cacheKey, content);

        return album;
    }

    /// <summary>
    /// Look up a track by its ISRC code.
    /// </summary>
    public async Task<TrackInfo?> LookupByIsrcAsync(string isrc, CancellationToken ct)
    {
        _logger.LogInformation("Looking up MusicBrainz track by ISRC: {Isrc}", isrc);

        // ISRC not found is not an error: GetContentAsync returns null for 404.
        var content = await GetContentAsync(BuildIsrcUri(isrc), "ISRC lookup", notFoundIsNull: true, ct);
        if (content is null)
            return null;

        var track = ParseIsrcLookupResponse(content);

        // Cache the result
        PutCached("isrc:" + NormalizeQuery(isrc), content);

        return track;
    }

    // Helper methods

    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sends a paced GET request. Retries 429 and 503 responses (honouring Retry-After) and transport
    /// failures with exponential backoff. Throws <see cref="MetadataUnavailableException"/> when the
    /// provider still cannot answer. Returns null for 404 when <paramref name="notFoundIsNull"/> is set.
    /// </summary>
    private async Task<string?> GetContentAsync(System.Uri uri, string operation, bool notFoundIsNull, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _gate.WaitForTurnAsync(ct);

            try
            {
                using var response = await _httpClient.GetAsync(uri, ct);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(ct);

                if (notFoundIsNull && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                if (!IsRetryable(response.StatusCode) || attempt == MaxAttempts)
                    throw new MetadataUnavailableException(operation, (int)response.StatusCode);

                var delay = RetryAfterDelay(response) ?? BackoffDelay(attempt);
                _gate.Penalize(delay);
                _logger.LogWarning(
                    "MusicBrainz returned {StatusCode} for {Operation}; attempt {Attempt} of {MaxAttempts} in {Delay}",
                    (int)response.StatusCode, operation, attempt + 1, MaxAttempts, delay);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                var delay = BackoffDelay(attempt);
                _logger.LogWarning(ex, "MusicBrainz request failed for {Operation}; attempt {Attempt} of {MaxAttempts} in {Delay}",
                    operation, attempt + 1, MaxAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new MetadataUnavailableException(operation, null, ex);
            }
        }
    }

    private static bool IsRetryable(System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.ServiceUnavailable
        || statusCode == System.Net.HttpStatusCode.TooManyRequests;

    private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        TimeSpan? delay = retryAfter.Delta;
        if (delay is null && retryAfter.Date is { } date)
            delay = date - DateTimeOffset.UtcNow;

        if (delay is null)
            return null;

        return delay.Value < TimeSpan.Zero ? TimeSpan.Zero : (delay.Value > MaxRetryDelay ? MaxRetryDelay : delay.Value);
    }

    private static TimeSpan BackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private bool TryGetCached(string key, out string content)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.CreatedAt <= CacheTtl)
            {
                content = entry.Content;
                return true;
            }
            _cache.TryRemove(key, out _);
        }

        content = string.Empty;
        return false;
    }

    private void PutCached(string key, string content) =>
        _cache[key] = new CacheEntry(content, DateTime.UtcNow);

    private static string NormalizeQuery(string value) => value.Trim();

    private sealed record CacheEntry(string Content, DateTime CreatedAt);

    private System.Uri BuildSearchUri(string resource, string query)
    {
        // Support artist:name style narrowing
        var cleanQuery = query.Trim();
        var encodedQuery = System.Web.HttpUtility.UrlEncode(cleanQuery);

        return new System.Uri($"https://musicbrainz.org/ws/2/{resource}?query={encodedQuery}&fmt=json", System.UriKind.Absolute);
    }

    private System.Uri BuildReleaseGroupUri(Guid releaseGroupMbid)
    {
        return new System.Uri($"https://musicbrainz.org/ws/2/release-group/{releaseGroupMbid}?inc=releases&fmt=json", System.UriKind.Absolute);
    }

    private System.Uri BuildReleaseUri(Guid releaseMbid)
    {
        return new System.Uri($"https://musicbrainz.org/ws/2/release/{releaseMbid}?inc=recordings+artist-credits+ratings&fmt=json", System.UriKind.Absolute);
    }

    private static Guid ParseFirstReleaseId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement.TryGetProperty("releases", out var releases)
            ? releases.EnumerateArray().FirstOrDefault()
            : default;
        return release.ValueKind == JsonValueKind.Object ? ReadGuid(release, "id") : Guid.Empty;
    }

    private System.Uri BuildIsrcUri(string isrc)
    {
        var encodedIsrc = System.Web.HttpUtility.UrlEncode(isrc);
        return new System.Uri($"https://musicbrainz.org/ws/2/track?isrc={encodedIsrc}&fmt=json", System.UriKind.Absolute);
    }

    private IReadOnlyList<TrackInfo> ParseTrackSearchResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadArray(document.RootElement, "recordings")
            .Select(recording => ParseTrack(recording))
            .ToList();
    }

    private IReadOnlyList<TrackInfo> ParseAlbumSearchResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadArray(document.RootElement, "releases")
            .Select(ParseAlbumSearchResult)
            .ToList();
    }

    private IReadOnlyList<TrackInfo> ParseArtistSearchResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadArray(document.RootElement, "artists")
            .Select(ParseArtistSearchResult)
            .ToList();
    }

    private AlbumDetail ParseAlbumDetailResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var recordings = new List<TrackInfo>();

        foreach (var medium in ReadArray(root, "media"))
        {
            foreach (var track in ReadArray(medium, "tracks"))
            {
                if (track.TryGetProperty("recording", out var recording))
                    recordings.Add(ParseTrack(recording, track));
            }
        }

        var artistCredits = ReadArtistCredits(root);
        return new AlbumDetail
        {
            Mbid = ReadGuid(root, "id"),
            Title = ReadString(root, "title") ?? string.Empty,
            Year = ReadYear(root),
            ArtistNames = artistCredits.Select(a => a.Name).ToList(),
            TrackTitles = recordings.Select(t => t.Title).ToList(),
            TrackMbids = recordings.Select(t => t.RecordingMbid).ToList(),
            Tracks = recordings
        };
    }

    private TrackInfo? ParseIsrcLookupResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var track = ReadArray(document.RootElement, "recordings").FirstOrDefault();
        return track.ValueKind == JsonValueKind.Undefined ? null : ParseTrack(track);
    }

    private static TrackInfo ParseTrack(JsonElement recording, JsonElement? releaseTrack = null)
    {
        var artists = ReadArtistCredits(recording);
        var release = ReadFirstRelease(recording);
        var releaseGroup = release.ValueKind == JsonValueKind.Object
            && release.TryGetProperty("release-group", out var group)
            && group.ValueKind == JsonValueKind.Object
                ? group
                : default;
        var trackNumber = releaseTrack.HasValue ? ReadInt(releaseTrack.Value, "position") : null;
        var releaseGroupMbid = releaseGroup.ValueKind == JsonValueKind.Object ? ReadGuid(releaseGroup, "id") : Guid.Empty;
        var year = releaseGroup.ValueKind == JsonValueKind.Object ? ReadYear(releaseGroup) : null;

        return new TrackInfo
        {
            RecordingMbid = ReadGuid(recording, "id"),
            Isrc = ReadArray(recording, "isrcs").Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            Title = ReadString(recording, "title") ?? string.Empty,
            ArtistName = artists.FirstOrDefault()?.Name,
            Artists = artists,
            AlbumTitle = release.ValueKind == JsonValueKind.Object ? ReadString(release, "title") : null,
            ReleaseGroupMbid = releaseGroupMbid == Guid.Empty ? null : releaseGroupMbid,
            Year = year ?? ReadYear(release),
            TrackNumber = trackNumber,
            DurationMs = ReadInt(recording, "length") ?? 0,
            MusicBrainzRating = ReadRating(recording)
        };
    }

    /// <summary>
    /// Search responses use "releases"; lookup responses use "release-list". Both carry the
    /// release title, date, and release-group ID.
    /// </summary>
    private static JsonElement ReadFirstRelease(JsonElement recording)
    {
        foreach (var propertyName in new[] { "release-list", "releases" })
        {
            if (!recording.TryGetProperty(propertyName, out var releases))
                continue;

            var first = releases.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                return first;
        }

        return default;
    }

    private static TrackInfo ParseAlbumSearchResult(JsonElement release)
    {
        var artists = ReadArtistCredits(release);
        var releaseGroup = release.TryGetProperty("release-group", out var group) ? group : default;
        var albumTitle = ReadString(release, "title") ?? string.Empty;
        return new TrackInfo
        {
            RecordingMbid = ReadGuid(releaseGroup.ValueKind == JsonValueKind.Object ? releaseGroup : release, "id"),
            Title = albumTitle,
            AlbumTitle = albumTitle,
            ArtistName = artists.FirstOrDefault()?.Name,
            Artists = artists
        };
    }

    private static TrackInfo ParseArtistSearchResult(JsonElement artist)
    {
        var name = ReadString(artist, "name") ?? string.Empty;
        var credit = new ArtistCredit(name, ReadGuid(artist, "id"));
        return new TrackInfo
        {
            RecordingMbid = credit.Mbid,
            Title = name,
            ArtistName = name,
            Artists = new[] { credit }
        };
    }

    private static IReadOnlyList<ArtistCredit> ReadArtistCredits(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return Array.Empty<ArtistCredit>();

        return credits.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("artist", out _))
            .Select(c =>
            {
                var artist = c.GetProperty("artist");
                return new ArtistCredit(
                    ReadString(c, "name") ?? ReadString(artist, "name") ?? string.Empty,
                    ReadGuid(artist, "id"));
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToList();
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static Guid ReadGuid(JsonElement element, string property)
    {
        return Guid.TryParse(ReadString(element, property), out var result) ? result : Guid.Empty;
    }

    private static int? ReadYear(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var date = ReadString(element, "first-release-date") ?? ReadString(element, "date");
        return date is not null && int.TryParse(date[..Math.Min(4, date.Length)], out var year) ? year : null;
    }

    private static double? ReadRating(JsonElement element)
    {
        if (!element.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object)
            return null;
        return rating.TryGetProperty("value", out var value) && value.TryGetDouble(out var result)
            ? result
            : null;
    }
}
