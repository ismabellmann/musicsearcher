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
    public async Task SearchTracksAsync_MapsReleaseGroupAndYearFromSearchReleaseShape()
    {
        const string json = """
        {
          "recordings": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Example Song",
              "artist-credit": [
                { "artist": { "id": "22222222-2222-2222-2222-222222222222", "name": "Artist One" } }
              ],
              "releases": [
                {
                  "title": "Example Album",
                  "date": "2020-06-01",
                  "release-group": {
                    "id": "55555555-5555-5555-5555-555555555555",
                    "title": "Example Album",
                    "first-release-date": "2019-03-04"
                  }
                }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(json);

        var track = Assert.Single(await client.SearchTracksAsync("Example Song", CancellationToken.None));

        Assert.Equal("Example Album", track.AlbumTitle);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), track.ReleaseGroupMbid);
        Assert.Equal(2019, track.Year);
    }

    [Fact]
    public async Task LookupByIsrcAsync_ReturnsNullWhenNoRecordingsExist()
    {
        var client = CreateClient("{ \"recordings\": [] }");

        var result = await client.LookupByIsrcAsync("US-AAA-24-00001", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchTracksAsync_ReusesFreshCacheEntry()
    {
        var handler = new CountingHandler("{ \"recordings\": [] }");
        var client = new MusicBrainzClient(
            new HttpClient(handler),
            NullLogger<MusicBrainzClient>.Instance);

        await client.SearchTracksAsync("same query", CancellationToken.None);
        await client.SearchTracksAsync("same query", CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAlbumAsync_ResolvesReleaseGroupThenReleaseRecordings()
    {
        var groupId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var releaseId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var handler = new SequenceHandler(
            $"{{\"releases\":[{{\"id\":\"{releaseId}\"}}]}}",
            $"{{\"id\":\"{releaseId}\",\"title\":\"Album\",\"artist-credit\":[{{\"artist\":{{\"id\":\"cccccccc-cccc-cccc-cccc-cccccccccccc\",\"name\":\"Artist\"}}}}],\"media\":[{{\"tracks\":[{{\"position\":1,\"recording\":{{\"id\":\"dddddddd-dddd-dddd-dddd-dddddddddddd\",\"title\":\"Song\",\"length\":180000,\"artist-credit\":[{{\"artist\":{{\"id\":\"cccccccc-cccc-cccc-cccc-cccccccccccc\",\"name\":\"Artist\"}}}}]}}}}]}}]}}");
        var client = new MusicBrainzClient(new HttpClient(handler), NullLogger<MusicBrainzClient>.Instance);

        var album = await client.GetAlbumAsync(groupId, CancellationToken.None);

        Assert.NotNull(album);
        Assert.Equal("Album", album!.Title);
        Assert.Single(album.Tracks);
        Assert.Equal(releaseId, handler.Requests[1].ReleaseId);
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

    private sealed class CountingHandler(string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }

    private sealed class SequenceHandler(string groupResponse, string releaseResponse) : HttpMessageHandler
    {
        public List<(string Path, Guid ReleaseId)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var id = Guid.TryParse(path.Split('/').Last(), out var parsed) ? parsed : Guid.Empty;
            Requests.Add((path, id));
            var content = Requests.Count == 1 ? groupResponse : releaseResponse;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
