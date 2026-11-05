using System.Text.RegularExpressions;

namespace MusicSync.Core;

/// <summary>
/// Validates configuration values that the application cannot run safely with.
/// </summary>
public static class MusicSyncOptionsValidator
{
    private static readonly Regex UserAgentPattern = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+ \(.+\)$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(
        MusicBrainzOptions musicBrainz,
        MatchingOptions matching,
        AudioOptions audio,
        WorkerOptions worker,
        AuthOptions? auth = null,
        CoverArtOptions? coverArt = null)
    {
        var errors = new List<string>();

        if (!UserAgentPattern.IsMatch(musicBrainz.UserAgent ?? string.Empty))
            errors.Add("MusicBrainz:UserAgent must have the form 'Name/Version (contact)'.");

        if (!IsUnitInterval(matching.AutoAcceptThreshold))
            errors.Add("Matching:AutoAcceptThreshold must be between 0 and 1.");

        if (!IsUnitInterval(matching.ReviewThreshold))
            errors.Add("Matching:ReviewThreshold must be between 0 and 1.");

        if (IsUnitInterval(matching.AutoAcceptThreshold)
            && IsUnitInterval(matching.ReviewThreshold)
            && matching.ReviewThreshold > matching.AutoAcceptThreshold)
        {
            errors.Add("Matching:ReviewThreshold cannot be greater than Matching:AutoAcceptThreshold.");
        }

        if (matching.DurationToleranceSeconds < 0)
            errors.Add("Matching:DurationToleranceSeconds cannot be negative.");

        if (!string.Equals(audio.Format, "mp3", StringComparison.OrdinalIgnoreCase))
            errors.Add("Audio:Format only supports 'mp3' in this version.");

        if (worker.Concurrency != 1)
            errors.Add("Worker:Concurrency must be 1 in this version.");

        if (worker.MaxAutomaticRetries < 0)
            errors.Add("Worker:MaxAutomaticRetries cannot be negative.");

        if (worker.RetryBaseDelaySeconds < 0)
            errors.Add("Worker:RetryBaseDelaySeconds cannot be negative.");

        if (auth is not null && (auth.SessionHours < 1 || auth.SessionHours > 720))
            errors.Add("Auth:SessionHours must be between 1 and 720.");

        if (coverArt is not null && (coverArt.MaxBytes < 1024 || coverArt.MaxBytes > 20_000_000))
            errors.Add("CoverArt:MaxBytes must be between 1024 and 20000000.");

        if (coverArt is not null && (coverArt.TimeoutSeconds < 1 || coverArt.TimeoutSeconds > 120))
            errors.Add("CoverArt:TimeoutSeconds must be between 1 and 120.");

        return errors;
    }

    private static bool IsUnitInterval(double value) => value >= 0 && value <= 1;
}
