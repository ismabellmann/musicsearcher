using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class CoverArtArchiveClientTests
{
    private const string UserAgent = "MusicSync/1.0 (test@example.com)";

    [Fact]
    public async Task GetFrontCoverAsync_ReturnsImageBytes()
    {
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal(
                "https://coverartarchive.org/release-group/11111111-1111-1111-1111-111111111111/front-500",
                request.RequestUri!.ToString());
            return ImageResponse(HttpStatusCode.OK, ImageBytes);
        });
        using var client = CreateClient(handler);

        var image = await client.GetFrontCoverAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CancellationToken.None);

        Assert.Equal(ImageBytes, image);
    }

    [Fact]
    public async Task GetFrontCoverAsync_ReturnsNullWhenNoCoverExists()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = CreateClient(handler);

        Assert.Null(await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetFrontCoverAsync_ReturnsNullForOversizeContentLength()
    {
        var handler = new ScriptedHandler(_ => ImageResponse(HttpStatusCode.OK, new byte[2048]));
        using var client = CreateClient(handler, new CoverArtOptions { MaxBytes = 1024 });

        Assert.Null(await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetFrontCoverAsync_ReturnsNullForNonImageContent()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not an image</html>")
        });
        using var client = CreateClient(handler);

        Assert.Null(await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetFrontCoverAsync_FollowsRedirectToArchiveOrg()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.Host == "coverartarchive.org"
            ? Redirect("https://ia801.archive.org/cover/example.jpg")
            : ImageResponse(HttpStatusCode.OK, ImageBytes));
        using var client = CreateClient(handler);

        var image = await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ImageBytes, image);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task GetFrontCoverAsync_RefusesRedirectOutsideAllowlist()
    {
        var handler = new ScriptedHandler(_ => Redirect("https://evil.example/cover.jpg"));
        using var client = CreateClient(handler);

        Assert.Null(await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task GetFrontCoverAsync_ReturnsNullWhenDisabled()
    {
        var handler = new ScriptedHandler(_ => ImageResponse(HttpStatusCode.OK, ImageBytes));
        using var client = CreateClient(handler, new CoverArtOptions { Enabled = false });

        Assert.Null(await client.GetFrontCoverAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    private static CoverArtArchiveClient CreateClient(HttpMessageHandler handler, CoverArtOptions? options = null)
        => new(
            options ?? new CoverArtOptions(),
            UserAgent,
            NullLogger<CoverArtArchiveClient>.Instance,
            handler);

    private static readonly byte[] ImageBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private static HttpResponseMessage ImageResponse(HttpStatusCode status, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(_respond(request));
        }
    }
}
