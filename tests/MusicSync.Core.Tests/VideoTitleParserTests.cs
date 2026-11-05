using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class VideoTitleParserTests
{
    [Theory]
    [InlineData("Pold - Hideout", "Pold", "Hideout")]
    [InlineData("Pold – Hideout", "Pold", "Hideout")]
    [InlineData("Pold - Hideout (Official Audio)", "Pold", "Hideout")]
    [InlineData("Pold - Hideout [Official Video]", "Pold", "Hideout")]
    [InlineData("Pold - Hideout (Lyrics)", "Pold", "Hideout")]
    [InlineData("Pold - Hideout (Official Audio) (Remastered 2024)", "Pold", "Hideout")]
    public void Parse_SplitsArtistAndTitle_AndDropsNoiseTags(string raw, string artist, string title)
    {
        var parsed = VideoTitleParser.Parse(raw);

        Assert.Equal(artist, parsed.Artist);
        Assert.Equal(title, parsed.Title);
    }

    [Theory]
    [InlineData("Hideout", "Hideout")]
    [InlineData("Hideout (Official Audio)", "Hideout")]
    [InlineData("Pold - ", "Pold -")]
    public void Parse_WithoutArtist_KeepsTitle(string raw, string title)
    {
        var parsed = VideoTitleParser.Parse(raw);

        Assert.Null(parsed.Artist);
        Assert.Equal(title, parsed.Title);
    }

    [Fact]
    public void Parse_Null_IsEmpty()
    {
        var parsed = VideoTitleParser.Parse(null);

        Assert.Null(parsed.Artist);
        Assert.Equal(string.Empty, parsed.Title);
    }
}
