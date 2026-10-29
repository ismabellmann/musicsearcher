using System;

namespace MusicSync.Core;

/// <summary>
/// Result of a match scoring operation, determining the fate of a candidate.
/// </summary>
public record ScoreResult
{
    /// <summary>
    /// Gets the numeric score (0.0 to 1.0).
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Gets whether the score meets the auto-accept threshold.
    /// </summary>
    public bool AutoAccept { get; init; }

    /// <summary>
    /// Gets whether the score is between review and auto-accept thresholds.
    /// </summary>
    public bool NeedsReview { get; init; }

    /// <summary>
    /// Gets the reason for the scoring decision.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}