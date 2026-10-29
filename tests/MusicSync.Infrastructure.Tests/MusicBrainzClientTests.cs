using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class MusicBrainzClientTests
{
    [Fact]
    public async Task SearchTracksAsync_MapsRecordingAndArtistCredits()
    {
        const string json = """
        {
          "recordings": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Example Song",
              "length": 201000,
              "isrcs": ["US-AAA-24-00001"],
              "artist-credit": [
                { "name": "Artist One", "artist": { "id": "22222222-2222-2222-2222-222222222222", "name": "Artist One" } },
                { "name": "Artist Two", "artist": { "id": "33333333-3333-3333-3333-333333333333", "name": "Artist Two" } }
              ],
              "release-list": [ { "title": "Example Album" } ]
            }
          ]
        }
        """;

        var client = CreateClient(json);

        var results = await client.SearchTracksAsync("Example Song", CancellationToken.None);

        var track = Assert.Single(results);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), track.RecordingMbid);
        Assert.Equal("Example Song", track.Title);
        Assert.Equal("Example Album", track.AlbumTitle);
        Assert.Equal(201000, track.DurationMs);
        Assert.Equal("US-AAA-24-00001", track.Isrc);
        Assert.Equal(2, track.Artists.Count);
        Assert.Equal("Artist One", track.ArtistName);
    }

    [Fact]
    public async Task SearchAlbumsAsync_MapsReleaseGroupAndArtists()
    {
        const string json = """
        {
          "releases": [
            {
              "id": "44444444-4444-4444-4444-444444444444",
              "title": "Example Album",
              "release-group": { "id": "55555555-5555-5555-5555-555555555555", "title": "Example Album" },
              "artist-credit": [
                { "artist": { "id": "66666666-6666-6666-6666-666666666666", "name": "Artist One" } }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(json);

        var results = await client.SearchAlbumsAsync("Example Album", CancellationToken.None);

        var album = Assert.Single(results);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), album.RecordingMbid);
        Assert.Equal("Example Album", album.AlbumTitle);
        Assert.Equal("Artist One", album.ArtistName);
    }

    [Fact]
    public async Task LookupByIsrcAsync_ReturnsNullWhenNoRecordingsExist()
    {
        var client = CreateClient("{ \"recordings\": [] }");

        var result = await client.LookupByIsrcAsync("US-AAA-24-00001", CancellationToken.None);

        Assert.Null(result);
    }

    private static MusicBrainzClient CreateClient(string json)
    {
        var handler = new StubHandler(json);
        var httpClient = new HttpClient(handler);
        return new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }
}
