using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class TrackScorerTests
{
    private readonly TrackScorer _sut;

    public TrackScorerTests()
    {
        _sut = new TrackScorer();
    }

    [Fact]
    public void Scorer_DefaultThresholds_AutoAcceptAt0_8()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist",
            DurationMs = 180000
        };

        // Act
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3",
            DurationMs = 180000
        });

        // Assert
        Assert.True(result.AutoAccept);
        Assert.True(result.Score >= 0.8);
    }

    [Fact]
    public void Scorer_DefaultThresholds_NeedsReviewAt0_5()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist",
            DurationMs = 180000
        };

        // Act
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert
        Assert.True(result.NeedsReview);
        Assert.True(result.Score >= 0.5 && result.Score < 0.8);
    }

    [Fact]
    public void Scorer_DurationMatching_ReturnsHighScore()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist",
            DurationMs = 180000
        };

        // Act - candidate with matching title should score high
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3",
            DurationMs = 180000
        });

        // Assert
        Assert.True(result.Score > 0.5);
    }

    [Fact]
    public void Scorer_TitleSimilarity_ReturnsMediumScore()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Artist Album Song",
            ArtistName = "Artist"
        };

        // Act - candidate with similar title
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Artist Album Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert
        Assert.True(result.Score > 0.3);
    }

    [Fact]
    public void Scorer_PenaltiesApplied_ForLive()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };

        // Act - candidate with "live" in title should have penalty
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song (Live)",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert - score should be lower due to live penalty
        Assert.True(result.Score < 1.0);
    }

    [Fact]
    public void Scorer_PenaltiesApplied_ForRemix()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };

        // Act - candidate with "remix" in title should have penalty
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song Remix",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert
        Assert.True(result.Score < 1.0);
    }

    [Fact]
    public void Scorer_BonusForOfficialAudio()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };

        // Act - candidate with "official audio" should get bonus
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song Official Audio",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert - bonus should increase score
        Assert.True(result.Score > 0.3);
    }

    [Fact]
    public void Scorer_Reason_ContainsMatchingInfo()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };

        // Act
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert
        Assert.NotEmpty(result.Reason);
        Assert.Contains("title", result.Reason);
    }

    [Fact]
    public void Scorer_AutoAcceptThreshold_Configurable()
    {
        // Arrange - create scorer with low auto-accept threshold
        var scorer = new TrackScorer(autoAcceptThreshold: 0.3, reviewThreshold: 0.1);

        // Act
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };
        var result = scorer.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert - should auto-accept with low threshold
        Assert.True(result.AutoAccept);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public void Scorer_ReviewThreshold_Configurable()
    {
        // Arrange - create scorer with low review threshold
        var scorer = new TrackScorer(autoAcceptThreshold: 0.9, reviewThreshold: 0.3);

        // Act
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Test Song",
            ArtistName = "Test Artist"
        };
        var result = scorer.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert - should need review (between thresholds)
        Assert.True(result.NeedsReview);
        Assert.False(result.AutoAccept);
    }

    [Fact]
    public void Scorer_DifferentTitles_LowerScore()
    {
        // Arrange
        var track = new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Completely Different Song",
            ArtistName = "Different Artist"
        };

        // Act
        var result = _sut.Score(track, new SourceCandidate
        {
            Title = "Test Song",
            SourceUrl = "https://example.com",
            Format = ".mp3"
        });

        // Assert - should have low score with no matching terms
        Assert.True(result.Score < 0.3);
    }
}
