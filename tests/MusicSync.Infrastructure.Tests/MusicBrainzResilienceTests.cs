using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public class MusicBrainzResilienceTests
{
    private const string OneRecording = """
        {"recordings":[{"id":"11111111-1111-1111-1111-111111111111","title":"Hideout","length":95000,
        "artist-credit":[{"name":"Pold","artist":{"id":"22222222-2222-2222-2222-222222222222","name":"Pold"}}]}]}
        """;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<int, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(_respond(Calls));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage Unavailable(int retryAfterSeconds = 0)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private static MusicBrainzClient CreateClient(ScriptedHandler handler, MusicBrainzRequestGate? gate = null) =>
        new(new HttpClient(handler), NullLogger<MusicBrainzClient>.Instance, null, gate);

    [Fact]
    public async Task ServiceUnavailable_IsRetried_ThenReturnsResults()
    {
        var handler = new ScriptedHandler(call => call < 3 ? Unavailable() : Json(HttpStatusCode.OK, OneRecording));

        var results = await CreateClient(handler).SearchTracksAsync("hideout", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Hideout", results[0].Title);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PersistentServiceUnavailable_ThrowsInsteadOfReturningEmpty()
    {
        var handler = new ScriptedHandler(_ => Unavailable());

        var error = await Assert.ThrowsAsync<MetadataUnavailableException>(() =>
            CreateClient(handler).SearchTracksAsync("hideout", CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task TooManyRequests_IsRetried()
    {
        var handler = new ScriptedHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json(HttpStatusCode.OK, OneRecording));

        var results = await CreateClient(handler).SearchTracksAsync("hideout", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task NonRetryableError_ThrowsWithoutRetrying()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<MetadataUnavailableException>(() =>
            CreateClient(handler).SearchTracksAsync("hideout", CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportFailure_IsRetriedThenReported()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<MetadataUnavailableException>(() =>
            CreateClient(handler).SearchTracksAsync("hideout", CancellationToken.None));

        Assert.Null(error.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("track", "/ws/2/recording")]
    [InlineData("album", "/ws/2/release")]
    [InlineData("artist", "/ws/2/artist")]
    public async Task Search_UsesTheMusicBrainzSearchEndpointForEachType(string type, string expectedPath)
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = CreateClient(handler);

        if (type == "track") await client.SearchTracksAsync("hideout", CancellationToken.None);
        else if (type == "album") await client.SearchAlbumsAsync("hideout", CancellationToken.None);
        else await client.SearchArtistsAsync("pold", CancellationToken.None);

        Assert.Equal(expectedPath, handler.LastUri?.AbsolutePath);
    }

    [Fact]
    public async Task IsrcNotFound_ReturnsNullWithoutRetrying()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var track = await CreateClient(handler).LookupByIsrcAsync("USXXX0000000", CancellationToken.None);

        Assert.Null(track);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SharedGate_PacesRequestsAcrossClientInstances()
    {
        var gate = new MusicBrainzRequestGate(TimeSpan.FromMilliseconds(500));
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{\"recordings\":[]}"));

        var first = CreateClient(handler, gate);
        var second = CreateClient(handler, gate);

        var clock = Stopwatch.StartNew();
        await first.SearchTracksAsync("a", CancellationToken.None);
        await second.SearchTracksAsync("b", CancellationToken.None);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds >= 450, $"Expected pacing between clients, took {clock.ElapsedMilliseconds} ms.");
    }
}
