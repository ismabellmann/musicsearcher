using Microsoft.Extensions.Logging.Abstractions;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public class ClientIdentityTests
{
    [Fact]
    public void MusicBrainzClient_SendsConfiguredUserAgent()
    {
        using var httpClient = new HttpClient();

        _ = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance,
            new MusicBrainzOptions { UserAgent = "Test/1.0 (test@example.com)" });

        Assert.Equal("Test/1.0 (test@example.com)", httpClient.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void MusicBrainzClient_DefaultUserAgentIsProjectIdentity()
    {
        using var httpClient = new HttpClient();

        _ = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);

        Assert.Contains("https://github.com/ismabellmann/musicsearcher", httpClient.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ListenBrainzClient_SharesTheConfiguredUserAgent()
    {
        using var httpClient = new HttpClient();

        _ = new ListenBrainzClient(httpClient, NullLogger<ListenBrainzClient>.Instance, null,
            new MusicBrainzOptions { UserAgent = "Test/1.0 (test@example.com)" });

        Assert.Equal("Test/1.0 (test@example.com)", httpClient.DefaultRequestHeaders.UserAgent.ToString());
    }
}
