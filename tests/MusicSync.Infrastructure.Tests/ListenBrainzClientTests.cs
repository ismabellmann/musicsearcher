using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class ListenBrainzClientTests
{
    [Fact]
    public async Task TopRecordingsForArtistAsync_MapsPayloadRecordings()
    {
        const string json = """
        {
          "payload": {
            "recordings": [
              {
                "recording_mbid": "11111111-1111-1111-1111-111111111111",
                "recording_name": "First Song",
                "total_listen_count": 42,
                "last_played": "2025-01-02T03:04:05Z"
              },
              {
                "recording_mbid": "22222222-2222-2222-2222-222222222222",
                "recording_name": "Second Song",
                "total_listen_count": 12
              }
            ]
          }
        }
        """;

        var client = CreateClient(json);
        var results = await client.TopRecordingsForArtistAsync(Guid.NewGuid(), 10, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("First Song", results[0].Title);
        Assert.Equal(42, results[0].PlayCount);
        Assert.Equal(1, results[0].Position);
        Assert.Equal(DateTime.Parse("2025-01-02T03:04:05Z").ToUniversalTime(), results[0].LastPlayed);
        Assert.Equal("Second Song", results[1].Title);
        Assert.Equal(2, results[1].Position);
    }

    [Fact]
    public async Task TopRecordingsForArtistAsync_RespectsRequestedCount()
    {
        const string json = """
        {
          "payload": {
            "recordings": [
              { "recording_mbid": "11111111-1111-1111-1111-111111111111", "recording_name": "First" },
              { "recording_mbid": "22222222-2222-2222-2222-222222222222", "recording_name": "Second" }
            ]
          }
        }
        """;

        var client = CreateClient(json);

        var results = await client.TopRecordingsForArtistAsync(Guid.NewGuid(), 1, CancellationToken.None);

        var recording = Assert.Single(results);
        Assert.Equal("First", recording.Title);
    }

    [Fact]
    public async Task TopRecordingsForArtistAsync_ReturnsEmptyForMissingPayload()
    {
        var client = CreateClient("{ \"payload\": { \"recordings\": [] } }");

        var results = await client.TopRecordingsForArtistAsync(Guid.NewGuid(), 10, CancellationToken.None);

        Assert.Empty(results);
    }

    private static ListenBrainzClient CreateClient(string json)
    {
        return new ListenBrainzClient(
            new HttpClient(new StubHandler(json)),
            NullLogger<ListenBrainzClient>.Instance);
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
