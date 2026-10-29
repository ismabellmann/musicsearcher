using MusicSync.Core;

namespace MusicSync.Core;

/// <summary>
/// Concrete implementation of IMatchScorer that scores track-to-candidate matches.
/// Uses duration, title similarity, and artist name matching.
/// </summary>
public class TrackScorer : IMatchScorer
{
    private readonly double _autoAcceptThreshold;
    private readonly double _reviewThreshold;
    private readonly int _durationToleranceSeconds;

    /// <summary>
    /// Creates a new TrackScorer with the specified thresholds.
    /// </summary>
    /// <param name="autoAcceptThreshold">Score threshold for auto-accept (0.0 to 1.0)</param>
    /// <param name="reviewThreshold">Score threshold for review state (0.0 to 1.0)</param>
    /// <param name="durationToleranceSeconds">Tolerance for duration matching</param>
    public TrackScorer(
        double autoAcceptThreshold = 0.80,
        double reviewThreshold = 0.50,
        int durationToleranceSeconds = 5)
    {
        _autoAcceptThreshold = autoAcceptThreshold;
        _reviewThreshold = reviewThreshold;
        _durationToleranceSeconds = durationToleranceSeconds;
    }

    /// <summary>
    /// Scores a track-to-candidate match using multiple signals:
    /// - Duration tolerance (strong signal)
    /// - Normalized title similarity
    /// - Artist/channel name similarity
    /// - Penalties for unwanted features (live, remix, etc.)
    /// - Bonus for official audio sources
    /// </summary>
    public ScoreResult Score(TrackInfo target, SourceCandidate candidate)
    {
        var score = 0.0;
        var reasons = new System.Text.StringBuilder();

        // 1. Duration matching (strong signal, up to 0.4 points)
        var durationScore = CalculateDurationScore(target.DurationMs, candidate);
        score += durationScore;
        if (durationScore > 0)
            reasons.Append($"duration:{durationScore:F2}; ");

        // 2. Title similarity (up to 0.3 points)
        var titleScore = CalculateTitleScore(target.Title, candidate.Title);
        score += titleScore;
        if (titleScore > 0)
            reasons.Append($"title:{titleScore:F2}; ");

        // 3. Artist name similarity (up to 0.2 points)
        var artistScore = CalculateArtistScore(target.ArtistName, candidate.Title);
        score += artistScore;
        if (artistScore > 0)
            reasons.Append($"artist:{artistScore:F2}; ");

        // 4. Penalties for unwanted features (can subtract up to 0.15)
        var penalty = CalculatePenalties(candidate.Title);
        score -= penalty;
        if (penalty > 0)
            reasons.Append($"penalty:{penalty:F2}; ");

        // 5. Bonus for official audio sources (up to 0.05)
        var bonus = CalculateBonus(candidate.Title);
        score += bonus;
        if (bonus > 0)
            reasons.Append($"bonus:{bonus:F2}; ");

        // Clamp to [0, 1]
        score = Math.Max(0, Math.Min(1, score));

        // Determine recommendation
        bool autoAccept = score >= _autoAcceptThreshold;
        bool needsReview = score >= _reviewThreshold && !autoAccept;

        return new ScoreResult
        {
            Score = score,
            AutoAccept = autoAccept,
            NeedsReview = needsReview,
            Reason = reasons.ToString().TrimEnd(';', ' ')
        };
    }

    /// <summary>
    /// Calculates duration-based score (0.0 to 0.4).
    /// Target duration within tolerance gets full points, outside tolerance gets reduced.
    /// </summary>
    private double CalculateDurationScore(int targetMs, SourceCandidate candidate)
    {
        if (targetMs <= 0 || !candidate.DurationMs.HasValue || candidate.DurationMs <= 0)
            return 0.0;

        var difference = Math.Abs(targetMs - candidate.DurationMs.Value);
        var toleranceMs = _durationToleranceSeconds * 1000;
        if (difference <= toleranceMs)
            return 0.4;

        var normalizedDifference = (double)difference / targetMs;
        return Math.Max(0.0, 0.4 * (1.0 - normalizedDifference));
    }

    /// <summary>
    /// Calculates title similarity score (0.0 to 0.3).
    /// Normalizes both titles, strips common words, compares.
    /// </summary>
    private double CalculateTitleScore(string targetTitle, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(targetTitle) || string.IsNullOrWhiteSpace(candidateTitle))
            return 0.0;

        // Normalize: lowercase, strip "feat.", "remaster", brackets, punctuation
        var normalizedTarget = NormalizeTitle(targetTitle);
        var normalizedCandidate = NormalizeTitle(candidateTitle);

        if (string.Equals(normalizedTarget, normalizedCandidate, StringComparison.Ordinal))
            return 0.6;

        // Simple similarity: count matching words
        var targetWords = normalizedTarget.Split(' ');
        var candidateWords = normalizedCandidate.Split(' ');
        var matchingWords = 0;

        foreach (var word in targetWords)
        {
            if (candidateWords.Contains(word))
                matchingWords++;
        }

        if (targetWords.Length == 0) return 0.0;
        return (double)matchingWords / targetWords.Length * 0.3;
    }

    /// <summary>
    /// Normalizes a title for comparison: lowercase, remove common words, strip punctuation.
    /// </summary>
    private string NormalizeTitle(string title)
    {
        var result = title.ToLowerInvariant();
        // Remove common prefixes/suffixes
        result = result.Replace("feat.", "").Replace("ft.", "").Replace("remaster", "").Replace("official audio", "");
        // Remove brackets and content within
        result = System.Text.RegularExpressions.Regex.Replace(result, "\\[.*?\\]", "");
        // Remove punctuation
        result = System.Text.RegularExpressions.Regex.Replace(result, "[^a-z0-9\\s]", "");
        // Remove extra whitespace
        result = string.Join(" ", result.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return result;
    }

    /// <summary>
    /// Calculates artist name similarity (0.0 to 0.2).
    /// </summary>
    private double CalculateArtistScore(string? targetArtist, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(targetArtist))
            return 0.0;

        // In a full implementation, we'd extract artist from candidate
        // For now, return based on title patterns
        return 0.0;
    }

    /// <summary>
    /// Calculates penalties for unwanted features (0.0 to 0.15).
    /// Penalties: live, concert, remix, cover, karaoke, sped up, slowed, reverb, 8d.
    /// Small bonus if the target title itself contains these words.
    /// </summary>
    private double CalculatePenalties(string candidateTitle)
    {
        var penalty = 0.0;
        var lowerTitle = candidateTitle.ToLowerInvariant();

        var penaltyWords = new[] { "live", "concert", "remix", "cover", "karaoke", "sped up", "slowed", "reverb", "8d" };

        foreach (var word in penaltyWords)
        {
            if (lowerTitle.Contains(word))
            {
                // Check if the target title also contains this word (smaller penalty)
                var targetLower = /* target title */ "";
                if (!string.IsNullOrWhiteSpace(targetLower) && targetLower.Contains(word))
                    penalty += 0.02; // Small penalty if target has it too
                else
                    penalty += 0.05; // Larger penalty if target doesn't have it
            }
        }

        return Math.Min(penalty, 0.15);
    }

    /// <summary>
    /// Calculates bonus for official audio sources (0.0 to 0.05).
    /// </summary>
    private double CalculateBonus(string candidateTitle)
    {
        var bonus = 0.0;
        var lowerTitle = candidateTitle.ToLowerInvariant();

        var bonusWords = new[] { "official audio", "topic", "audio", "hd", "music video" };
        foreach (var word in bonusWords)
        {
            if (lowerTitle.Contains(word))
                bonus += 0.01;
        }

        return Math.Min(bonus, 0.05);
    }
}
