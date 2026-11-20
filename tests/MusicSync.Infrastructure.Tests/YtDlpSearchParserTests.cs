using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public class YtDlpSearchParserTests
{
    [Fact]
    public void Parse_ValidLines_ReturnsVideosWithDurationInMilliseconds()
    {
        const string output = """
            {"id":"Fozjmwn3htg","title":"Hideout - Pold","duration":95.4}
            {"id":"abcdefghijk","title":"Other","duration":null}
            """;

        var videos = YtDlpSearchParser.Parse(output, 5);

        Assert.Equal(2, videos.Count);
        Assert.Equal("Fozjmwn3htg", videos[0].VideoId);
        Assert.Equal("Hideout - Pold", videos[0].Title);
        Assert.Equal(95400, videos[0].DurationMs);
        Assert.Null(videos[1].DurationMs);
    }

    [Fact]
    public void Parse_InvalidIdsAndMalformedLines_AreSkipped()
    {
        const string output = """
            {"id":"--help","title":"option-like"}
            {"id":"short","title":"bad length"}
            not json at all
            {"title":"no id"}
            {"id":"Fozjmwn3htg","title":"Valid"}
            """;

        var videos = YtDlpSearchParser.Parse(output, 5);

        Assert.Single(videos);
        Assert.Equal("Fozjmwn3htg", videos[0].VideoId);
    }

    [Fact]
    public void Parse_RespectsMaximum()
    {
        const string output = """
            {"id":"aaaaaaaaaaa","title":"one"}
            {"id":"bbbbbbbbbbb","title":"two"}
            {"id":"ccccccccccc","title":"three"}
            """;

        Assert.Equal(2, YtDlpSearchParser.Parse(output, 2).Count);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Fozjmwn3htg", true)]
    [InlineData("https://www.youtube.com/watch?v=Fozjmwn3htg&list=PL1", false)]
    [InlineData("https://evil.example.com/watch?v=Fozjmwn3htg", false)]
    [InlineData("--exec=rm -rf /", false)]
    public void IsWatchUrl_AcceptsOnlyCanonicalYouTubeWatchUrls(string url, bool expected)
    {
        Assert.Equal(expected, YtDlpSearchParser.IsWatchUrl(url));
    }

    [Fact]
    public void ToWatchUrl_RejectsUnsafeIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => YtDlpSearchParser.ToWatchUrl("--help"));
    }

    [Fact]
    public void ParseMetadata_MapsSingleVideoFields()
    {
        var metadata = YtDlpSearchParser.ParseMetadata(
            "{\"id\":\"Fozjmwn3htg\",\"title\":\"Artist - Song\",\"uploader\":\"Artist Channel\",\"duration\":201.5}",
            "https://www.youtube.com/watch?v=Fozjmwn3htg");

        Assert.Equal("Fozjmwn3htg", metadata.VideoId);
        Assert.Equal("Artist - Song", metadata.RawTitle);
        Assert.Equal("Artist Channel", metadata.Uploader);
        Assert.Equal(201500, metadata.DurationMs);
        Assert.Equal("https://www.youtube.com/watch?v=Fozjmwn3htg", metadata.Url);
    }
}
