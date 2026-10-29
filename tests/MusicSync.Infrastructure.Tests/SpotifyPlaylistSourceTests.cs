using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class SpotifyPlaylistSourceTests
{
    [Fact]
    public async Task GetPlaylistAsync_MapsPlaylistAndTrackItems()
    {
        const string playlistJson = """
        {
          "id": "playlist-1",
          "name": "Example Playlist",
          "total_tracks": 1,
          "owner": { "display_name": "Example User" },
          "tracks": {
            "items": [
              {
                "track": {
                  "id": "track-1",
                  "name": "Example Song",
                  "track_number": 3,
                  "duration_ms": 201000,
                  "explicit": true,
                  "artists": [{ "name": "Artist One" }, { "name": "Artist Two" }],
                  "album": { "name": "Example Album" }
                }
              }
            ]
          }
        }
        """;

        var client = CreateClient(playlistJson);

        var playlist = await client.GetPlaylistAsync("playlist-1", CancellationToken.None);

        Assert.Equal("playlist-1", playlist.Id);
        Assert.Equal("Example Playlist", playlist.Name);
        Assert.Equal(1, playlist.TotalTracks);
        Assert.Equal("Example User", playlist.OwnerDisplayName);

        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("track-1", track.Id);
        Assert.Equal("Example Song", track.Name);
        Assert.Equal("Artist One, Artist Two", track.ArtistName);
        Assert.Equal("Example Album", track.AlbumName);
        Assert.Equal(3, track.TrackNumber);
        Assert.Equal(201000, track.DurationMs);
        Assert.True(track.IsExplicit);
    }

    [Fact]
    public async Task GetPlaylistAsync_MapsEmptyItems()
    {
        var client = CreateClient("{ \"id\": \"empty\", \"name\": \"Empty\", \"tracks\": { \"items\": [] } }");

        var playlist = await client.GetPlaylistAsync("empty", CancellationToken.None);

        Assert.Equal("empty", playlist.Id);
        Assert.Equal("Empty", playlist.Name);
        Assert.Empty(playlist.Tracks);
        Assert.Equal(0, playlist.TotalTracks);
    }

    private static SpotifyPlaylistSource CreateClient(string playlistJson)
    {
        return new SpotifyPlaylistSource(
            new HttpClient(new StubHandler(playlistJson)),
            NullLogger<SpotifyPlaylistSource>.Instance,
            "client-id",
            "client-secret");
    }

    private sealed class StubHandler(string playlistJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.Host == "accounts.spotify.com")
            {
                return Task.FromResult(JsonResponse("{ \"access_token\": \"test-token\", \"expires_in\": 3600 }"));
            }

            return Task.FromResult(JsonResponse(playlistJson));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        }
    }
}
