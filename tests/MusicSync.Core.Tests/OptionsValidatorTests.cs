using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class OptionsValidatorTests
{
    private static IReadOnlyList<string> Validate(
        string userAgent = "MusicSync/0.1 (https://example.com/contact)",
        double autoAccept = 0.80,
        double review = 0.50,
        int tolerance = 5,
        string format = "mp3",
        int concurrency = 1)
    {
        return MusicSyncOptionsValidator.Validate(
            new MusicBrainzOptions { UserAgent = userAgent },
            new MatchingOptions { AutoAcceptThreshold = autoAccept, ReviewThreshold = review, DurationToleranceSeconds = tolerance },
            new AudioOptions { Format = format },
            new WorkerOptions { Concurrency = concurrency });
    }

    [Fact]
    public void DefaultOptions_AreValid()
    {
        Assert.Empty(MusicSyncOptionsValidator.Validate(
            new MusicBrainzOptions(), new MatchingOptions(), new AudioOptions(), new WorkerOptions()));
    }

    [Theory]
    [InlineData("MusicSync/0.1 (https://example.com)", true)]
    [InlineData("MusicSync/0.1 (you@example.com)", true)]
    [InlineData("MusicSync (you@example.com)", false)]
    [InlineData("MusicSync/0.1", false)]
    [InlineData("", false)]
    [InlineData("MusicSync/0.1 ()", false)]
    public void UserAgent_MustHaveNameVersionAndContact(string userAgent, bool valid)
    {
        var errors = Validate(userAgent: userAgent);

        Assert.Equal(valid, errors.All(error => !error.Contains("UserAgent", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1.1, 0.5)]
    [InlineData(0.8, -0.1)]
    [InlineData(double.NaN, 0.5)]
    public void Thresholds_OutsideUnitInterval_AreRejected(double autoAccept, double review)
    {
        Assert.NotEmpty(Validate(autoAccept: autoAccept, review: review));
    }

    [Fact]
    public void ReviewThreshold_AboveAutoAccept_IsRejected()
    {
        var errors = Validate(autoAccept: 0.6, review: 0.7);

        Assert.Contains(errors, error => error.Contains("ReviewThreshold cannot be greater", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeDurationTolerance_IsRejected()
    {
        Assert.NotEmpty(Validate(tolerance: -1));
    }

    [Theory]
    [InlineData("flac", false)]
    [InlineData("MP3", true)]
    public void AudioFormat_OnlyMp3IsSupported(string format, bool valid)
    {
        var errors = Validate(format: format);

        Assert.Equal(valid, errors.All(error => !error.Contains("Audio:Format", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void WorkerConcurrency_MustBeOne(int concurrency)
    {
        Assert.Contains(Validate(concurrency: concurrency), error => error.Contains("Worker:Concurrency", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(720, true)]
    [InlineData(721, false)]
    public void SessionHours_MustBeBetween1And720(int hours, bool valid)
    {
        var errors = MusicSyncOptionsValidator.Validate(
            new MusicBrainzOptions(), new MatchingOptions(), new AudioOptions(), new WorkerOptions(),
            new AuthOptions { SessionHours = hours });

        Assert.Equal(valid, errors.All(error => !error.Contains("SessionHours", StringComparison.Ordinal)));
    }
}
