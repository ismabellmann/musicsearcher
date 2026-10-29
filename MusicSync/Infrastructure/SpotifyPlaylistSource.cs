using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

/// <summary>
/// Spotify public playlist source.
/// Uses Client Credentials flow (read-only, no user OAuth tokens).
/// Section 8.3 of PROJECT_CONTEXT.md specifies this approach.
/// </summary>
public class SpotifyPlaylistSource : IPlaylistSource
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyPlaylistSource> _logger;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public SpotifyPlaylistSource(HttpClient httpClient, ILogger<SpotifyPlaylistSource> logger,
        string clientId, string clientSecret)
    {
        _httpClient = httpClient;
        _logger = logger;
        _clientId = clientId;
        _clientSecret = clientSecret;

        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Name => "Spotify Playlists";

    /// <summary>
    /// Get playlist information from a public Spotify playlist URL or ID.
    /// </summary>
    public async Task<PlaylistInfo> GetPlaylistAsync(string urlOrId, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);

        _logger.LogInformation("Fetching Spotify playlist: {UrlOrId}", urlOrId);

        try
        {
            var requestUri = BuildPlaylistUri(urlOrId);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken!);

            var response = await _httpClient.GetAsync(requestUri, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spotify API returned {StatusCode} for playlist {Id}", response.StatusCode, urlOrId);
                throw new HttpRequestException($"Spotify API error: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            return ParsePlaylistResponse(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Spotify playlist fetch failed: {UrlOrId}", urlOrId);
            throw;
        }
    }

    private async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        // Refresh token if needed
        if (_accessToken == null || DateTime.UtcNow >= _tokenExpiry)
        {
            await RefreshAccessTokenAsync(ct);
        }
    }

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        var tokenEndpoint = "https://accounts.spotify.com/api/token";

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("client_secret", _clientSecret)
        });

        var response = await _httpClient.PostAsync(tokenEndpoint, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Spotify token refresh failed: {StatusCode} - {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Failed to obtain Spotify access token: {response.StatusCode}");
        }

        var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(await response.Content.ReadAsStringAsync(ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        _accessToken = tokenResponse?.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse?.ExpiresIn ?? 3600);

        _logger.LogInformation("Spotify access token obtained, expires in {Seconds} seconds", tokenResponse?.ExpiresIn ?? 3600);
    }

    private System.Uri BuildPlaylistUri(string urlOrId)
    {
        // Accept either a full URL or just the playlist ID
        var id = urlOrId;

        // Extract ID from URL if needed
        if (urlOrId.Contains("spotify.com"))
        {
            // Extract playlist ID from URL patterns
            var segments = urlOrId.Split('/');
            // Look for the playlist segment
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "playlist" && i + 1 < segments.Length)
                {
                    id = segments[i + 1];
                    break;
                }
            }
        }

        return new System.Uri($"https://api.spotify.com/v2/playlists/{id}?fields=id,name,total_tracks,owner.display_name,tracks.track_number,tracks.duration_ms,tracks.is_explicit,tracks.artists.name,tracks.album.name", System.UriKind.Absolute);
    }

    private PlaylistInfo ParsePlaylistResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tracks = new List<PlaylistTrack>();

        if (root.TryGetProperty("tracks", out var tracksContainer))
        {
            foreach (var item in ReadArray(tracksContainer, "items"))
            {
                if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
                    tracks.Add(ParsePlaylistTrack(track));
            }
        }

        var owner = root.TryGetProperty("owner", out var ownerElement)
            ? ReadString(ownerElement, "display_name")
            : null;

        return new PlaylistInfo
        {
            Id = ReadString(root, "id") ?? string.Empty,
            Name = ReadString(root, "name") ?? string.Empty,
            TotalTracks = ReadInt(root, "total_tracks") ?? tracks.Count,
            OwnerDisplayName = owner,
            Tracks = tracks
        };
    }

    private static PlaylistTrack ParsePlaylistTrack(JsonElement track)
    {
        var artists = ReadArray(track, "artists")
            .Select(artist => ReadString(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name));

        var albumName = track.TryGetProperty("album", out var album)
            ? ReadString(album, "name")
            : null;

        return new PlaylistTrack
        {
            Id = ReadString(track, "id") ?? string.Empty,
            Name = ReadString(track, "name") ?? string.Empty,
            ArtistName = string.Join(", ", artists),
            AlbumName = albumName,
            TrackNumber = ReadInt(track, "track_number") ?? 0,
            DurationMs = ReadInt(track, "duration_ms") ?? 0,
            IsExplicit = ReadBool(track, "explicit")
        };
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

    private static bool? ReadBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
    }

    private class SpotifyTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public int ExpiresIn { get; set; }
        public string TokenType { get; set; } = "Bearer";
    }
}
