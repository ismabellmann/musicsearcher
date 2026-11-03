using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class ScorerSelectionTests
{
    private readonly TrackScorer _sut = new();

    private static TrackInfo Target(string title, string artist = "Pold", int durationMs = 95000) => new()
    {
        RecordingMbid = Guid.NewGuid(),
        Title = title,
        ArtistName = artist,
        DurationMs = durationMs
    };

    private static SourceCandidate Candidate(string title, int? durationMs = 95000) => new()
    {
        Title = title,
        SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa",
        DurationMs = durationMs
    };

    [Fact]
    public void ArtistInTitle_OutranksExactTitleWithoutArtist()
    {
        var track = Target("Hideout");

        var withArtist = _sut.Score(track, Candidate("Hideout - Pold | Royalty Free Music"));
        var exactTitleOnly = _sut.Score(track, Candidate("Hideout"));

        Assert.True(withArtist.Score > exactTitleOnly.Score);
        Assert.True(withArtist.AutoAccept);
        Assert.False(exactTitleOnly.AutoAccept);
    }

    [Fact]
    public void ExactTitleAndDurationAlone_DoNotReachAutoAccept()
    {
        var result = _sut.Score(Target("Hideout"), Candidate("Hideout"));

        Assert.False(result.AutoAccept);
        Assert.InRange(result.Score, 0.69, 0.71);
    }

    [Theory]
    [InlineData("Song (Live at Arena)", "Song", true)]
    [InlineData("Song (Remix)", "Song", true)]
    [InlineData("Song (Remix)", "Song (Remix)", false)]
    [InlineData("Delivery", "Delivery", false)]
    [InlineData("Song Live", "Song Live", false)]
    public void Penalties_ApplyOnlyWhenTargetLacksTheWord(string candidateTitle, string targetTitle, bool expectPenalty)
    {
        var result = _sut.Score(Target(targetTitle, artist: "Someone"), Candidate(candidateTitle));

        Assert.Equal(expectPenalty, result.Reason.Contains("penalty:", StringComparison.Ordinal));
    }

    [Fact]
    public void ArtistMatch_RequiresWholeWords()
    {
        var result = _sut.Score(Target("Hideout", artist: "Pold"), Candidate("Hideout - Polder Remix Pack"));

        Assert.DoesNotContain("artist:", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistMatch_IsCaseInsensitiveAndIgnoresPunctuation()
    {
        var result = _sut.Score(Target("Hideout", artist: "Pold"), Candidate("hideout | POLD."));

        Assert.Contains("artist:0.20", result.Reason, StringComparison.Ordinal);
    }
}
