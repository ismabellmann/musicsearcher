using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Logging;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

/// <summary>
/// Resolves public Spotify playlist pages without credentials or Spotify API access.
/// The embed page contains a JSON state payload with the public track list.
/// </summary>
public sealed class SpotifyPlaylistSource : IPlaylistSource
{
    private static readonly Regex NextData = new(
        "<script[^>]+id=[\"']__NEXT_DATA__[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyPlaylistSource> _logger;

    public SpotifyPlaylistSource(HttpClient httpClient, ILogger<SpotifyPlaylistSource> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MusicSync/0.1");
    }

    public async Task<PlaylistInfo> GetPlaylistAsync(string urlOrId, CancellationToken ct)
    {
        var playlistId = ParsePlaylistId(urlOrId);
        var uri = new Uri($"https://open.spotify.com/embed/playlist/{playlistId}");
        _logger.LogInformation("Fetching public Spotify playlist {PlaylistId}.", playlistId);

        using var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var match = NextData.Match(html);
        if (!match.Success)
            throw new InvalidOperationException("Spotify no devolvió los datos públicos de la lista.");

        var json = WebUtility.HtmlDecode(match.Groups["json"].Value);
        using var document = JsonDocument.Parse(json);
        var entity = document.RootElement
            .GetProperty("props").GetProperty("pageProps")
            .GetProperty("state").GetProperty("data")
            .GetProperty("entity");

        var tracks = new List<PlaylistTrack>();
        if (entity.TryGetProperty("trackList", out var trackList))
        {
            foreach (var track in trackList.EnumerateArray())
            {
                var uriValue = ReadString(track, "uri") ?? string.Empty;
                var id = uriValue.StartsWith("spotify:track:", StringComparison.Ordinal)
                    ? uriValue[14..]
                    : uriValue;
                if (string.IsNullOrWhiteSpace(id) || !string.Equals(ReadString(track, "entityType"), "track", StringComparison.OrdinalIgnoreCase))
                    continue;

                var artists = (ReadString(track, "subtitle") ?? string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                tracks.Add(new PlaylistTrack
                {
                    Id = id,
                    Name = ReadString(track, "title") ?? string.Empty,
                    ArtistName = string.Join(", ", artists),
                    DurationMs = ReadInt(track, "duration") ?? 0,
                    IsExplicit = ReadBool(track, "isExplicit")
                });
            }
        }

        return new PlaylistInfo
        {
            Id = ReadString(entity, "id") ?? playlistId,
            Name = ReadString(entity, "name") ?? ReadString(entity, "title") ?? string.Empty,
            TotalTracks = tracks.Count,
            OwnerDisplayName = ReadString(entity, "subtitle"),
            Tracks = tracks
        };
    }

    public static string ParsePlaylistId(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "open.spotify.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Se requiere una URL pública de playlist de Spotify.", nameof(value));

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, segment => string.Equals(segment, "playlist", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= segments.Length || string.IsNullOrWhiteSpace(segments[index + 1]))
            throw new ArgumentException("La URL no contiene un identificador de playlist válido.", nameof(value));

        var id = segments[index + 1];
        if (id.Length > 100 || id.Any(character => !char.IsLetterOrDigit(character)))
            throw new ArgumentException("El identificador de playlist no es válido.", nameof(value));
        return id;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool? ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean()
                : null;
}
