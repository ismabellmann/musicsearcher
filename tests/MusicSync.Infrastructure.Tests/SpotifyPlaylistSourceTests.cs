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
        {"props":{"pageProps":{"state":{"data":{"entity":{"id":"playlist1","name":"Example Playlist","subtitle":"Example User","trackList":[{"uri":"spotify:track:track1","entityType":"track","title":"Example Song","subtitle":"Artist One, Artist Two","duration":201000,"isExplicit":true}]}}}}}}
        """;

        var client = CreateClient(playlistJson);

        var playlist = await client.GetPlaylistAsync("https://open.spotify.com/playlist/playlist1", CancellationToken.None);

        Assert.Equal("playlist1", playlist.Id);
        Assert.Equal("Example Playlist", playlist.Name);
        Assert.Equal(1, playlist.TotalTracks);
        Assert.Equal("Example User", playlist.OwnerDisplayName);

        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("track1", track.Id);
        Assert.Equal("Example Song", track.Name);
        Assert.Equal("Artist One, Artist Two", track.ArtistName);
        Assert.Null(track.AlbumName);
        Assert.Equal(0, track.TrackNumber);
        Assert.Equal(201000, track.DurationMs);
        Assert.True(track.IsExplicit);
    }

    [Fact]
    public async Task GetPlaylistAsync_MapsEmptyItems()
    {
        var client = CreateClient("{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":{\"id\":\"empty\",\"name\":\"Empty\",\"trackList\":[]}}}}}}");

        var playlist = await client.GetPlaylistAsync("https://open.spotify.com/playlist/empty", CancellationToken.None);

        Assert.Equal("empty", playlist.Id);
        Assert.Equal("Empty", playlist.Name);
        Assert.Empty(playlist.Tracks);
        Assert.Equal(0, playlist.TotalTracks);
    }

    private static SpotifyPlaylistSource CreateClient(string playlistJson)
    {
        return new SpotifyPlaylistSource(
            new HttpClient(new StubHandler(playlistJson)),
            NullLogger<SpotifyPlaylistSource>.Instance);
    }

    private sealed class StubHandler(string playlistJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<html><script id=\"__NEXT_DATA__\">{playlistJson}</script></html>")
            });
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
