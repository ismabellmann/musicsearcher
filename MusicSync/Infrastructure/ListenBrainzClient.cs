using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

/// <summary>
/// ListenBrainz popularity provider.
/// Provides play count data for artist ranking and track prioritization.
/// Section 8.7 of PROJECT_CONTEXT.md mentions optional ListenBrainz user token.
/// </summary>
public class ListenBrainzClient : IPopularityProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ListenBrainzClient> _logger;
    private readonly string? _userToken;
    private readonly TimeSpan _rateInterval = TimeSpan.FromMilliseconds(200); // ~5 rps max
    private DateTime _lastRequestTime = DateTime.MinValue;

    public ListenBrainzClient(HttpClient httpClient, ILogger<ListenBrainzClient> logger, IConfiguration? configuration = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _userToken = configuration?.GetValue<string>("ListenBrainz__Token");

        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // MusicBrainz-compatible User-Agent
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MusicSync/1.0 ( music-sync@example.com )");
        }
    }

    /// <summary>
    /// Get top recordings for an artist, ranked by play count.
    /// </summary>
    public async Task<IReadOnlyList<RankedRecording>> TopRecordingsForArtistAsync(Guid artistMbid, int count, CancellationToken ct)
    {
        await RateLimitAsync();

        _logger.LogInformation("Fetching ListenBrainz top recordings for artist: {Mbid}", artistMbid);

        try
        {
            var requestUri = BuildArtistTopUri(artistMbid, count);
            var response = await _httpClient.GetAsync(requestUri, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ListenBrainz API returned {StatusCode} for artist {Mbid}",
                    response.StatusCode, artistMbid);
                return System.Array.Empty<RankedRecording>();
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var recordings = ParseTopRecordingsResponse(content);

            // Take only the requested count
            return recordings.Take(count).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ListenBrainz top recordings failed for artist: {Mbid}", artistMbid);
            return System.Array.Empty<RankedRecording>();
        }
    }

    private async Task RateLimitAsync()
    {
        // Token bucket rate limiter: ~5 requests per second max
        var timeSinceLast = DateTime.UtcNow - _lastRequestTime;
        if (timeSinceLast < _rateInterval)
        {
            await Task.Delay(_rateInterval - timeSinceLast, CancellationToken.None);
        }

        _lastRequestTime = DateTime.UtcNow;
    }

    private System.Uri BuildArtistTopUri(Guid artistMbid, int count)
    {
        // ListenBrainz API endpoint for top recordings
        var mbid = artistMbid.ToString();
        return new System.Uri($"https://api.listenbrainz.org/1.0/artist/{mbid}/top?limit={count}", System.UriKind.Absolute);
    }

    private IReadOnlyList<RankedRecording> ParseTopRecordingsResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var recordingsRoot = root.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("recordings", out var payloadRecordings)
            ? payloadRecordings
            : root.TryGetProperty("recordings", out var rootRecordings)
                ? rootRecordings
                : default;

        if (recordingsRoot.ValueKind != JsonValueKind.Array)
            return Array.Empty<RankedRecording>();

        return recordingsRoot.EnumerateArray()
            .Select((recording, index) => new RankedRecording
            {
                RecordingMbid = ReadGuid(recording, "recording_mbid"),
                Title = ReadString(recording, "recording_name")
                    ?? ReadString(recording, "title")
                    ?? string.Empty,
                PlayCount = ReadInt(recording, "total_listen_count")
                    ?? ReadInt(recording, "play_count")
                    ?? ReadInt(recording, "listen_count")
                    ?? 0,
                Position = index + 1,
                LastPlayed = ReadDateTime(recording, "last_played")
                    ?? ReadDateTime(recording, "last_listened_at")
            })
            .Where(recording => recording.RecordingMbid != Guid.Empty)
            .ToList();
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

    private static DateTime? ReadDateTime(JsonElement element, string property)
    {
        return DateTime.TryParse(
            ReadString(element, property),
            null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var result)
            ? result
            : null;
    }
}
