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
        WorkerOptions worker)
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

        return errors;
    }

    private static bool IsUnitInterval(double value) => value >= 0 && value <= 1;
}
