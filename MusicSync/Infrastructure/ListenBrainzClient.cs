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
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private const int MaxAttempts = 3;

    public ListenBrainzClient(
        HttpClient httpClient,
        ILogger<ListenBrainzClient> logger,
        IConfiguration? configuration = null,
        MusicBrainzOptions? identity = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _userToken = configuration?["ListenBrainz:Token"];

        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Same identifying User-Agent as the MusicBrainz client
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd((identity ?? new MusicBrainzOptions()).UserAgent);
    }

    /// <summary>
    /// Get top recordings for an artist, ranked by play count.
    /// </summary>
    public async Task<IReadOnlyList<RankedRecording>> TopRecordingsForArtistAsync(Guid artistMbid, int count, CancellationToken ct)
    {
        _logger.LogInformation("Fetching ListenBrainz top recordings for artist: {Mbid}", artistMbid);

        for (var attempt = 1; ; attempt++)
        {
            await RateLimitAsync(ct);
            try
            {
                var requestUri = BuildArtistTopUri(artistMbid, count);
                using var response = await _httpClient.GetAsync(requestUri, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return System.Array.Empty<RankedRecording>();

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryable(response.StatusCode) && attempt < MaxAttempts)
                    {
                        var delay = RetryAfterDelay(response) ?? BackoffDelay(attempt);
                        _logger.LogWarning(
                            "ListenBrainz returned {StatusCode}; attempt {Attempt} of {MaxAttempts} in {Delay}.",
                            (int)response.StatusCode, attempt, MaxAttempts, delay);
                        await Task.Delay(delay, ct);
                        continue;
                    }

                    throw new MetadataUnavailableException("ListenBrainz top recordings", (int)response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                var recordings = ParseTopRecordingsResponse(content);

                return recordings.Take(count).ToList();
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                var delay = BackoffDelay(attempt);
                _logger.LogWarning(ex, "ListenBrainz request failed; attempt {Attempt} of {MaxAttempts} in {Delay}.", attempt, MaxAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new MetadataUnavailableException("ListenBrainz top recordings", null, ex);
            }
        }
    }

    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLock.WaitAsync(ct);
        try
        {
            var timeSinceLast = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLast < _rateInterval)
                await Task.Delay(_rateInterval - timeSinceLast, ct);

            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private static bool IsRetryable(System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ClampDelay(delta);
        if (retryAfter?.Date is { } date)
            return ClampDelay(date - DateTimeOffset.UtcNow);
        return null;
    }

    private static TimeSpan BackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    private static TimeSpan ClampDelay(TimeSpan delay) =>
        delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : delay;

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
