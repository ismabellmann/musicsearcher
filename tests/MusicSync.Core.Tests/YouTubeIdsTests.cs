using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class YouTubeIdsTests
{
    [Fact]
    public void TryGetVideoId_AcceptsWatchUrls_Only()
    {
        Assert.True(YouTubeIds.TryGetVideoId("https://www.youtube.com/watch?v=Fozjmwn3htg", out var id));
        Assert.Equal("Fozjmwn3htg", id);
        Assert.False(YouTubeIds.TryGetVideoId("https://www.youtube.com/watch?v=Fozjmwn3htg&list=PL1", out _));
        Assert.False(YouTubeIds.TryGetVideoId("https://example.com/watch?v=Fozjmwn3htg", out _));
        Assert.False(YouTubeIds.TryGetVideoId("musicbrainz://recording/x", out _));
    }

    [Fact]
    public void RecordingIdFor_IsStableAndDistinctPerVideo()
    {
        Assert.Equal(YouTubeIds.RecordingIdFor("Fozjmwn3htg"), YouTubeIds.RecordingIdFor("Fozjmwn3htg"));
        Assert.NotEqual(YouTubeIds.RecordingIdFor("Fozjmwn3htg"), YouTubeIds.RecordingIdFor("5xrUFZDhSwQ"));
    }
}
