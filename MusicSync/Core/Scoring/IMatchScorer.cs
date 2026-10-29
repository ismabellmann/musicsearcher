using MusicSync.Core;

namespace MusicSync.Core;

/// <summary>
/// Interface for scoring track-to-candidate matches.
/// Pure function, no I/O, heavily unit-tested.
/// </summary>
public interface IMatchScorer
{
    /// <summary>
    /// Scores a track-to-candidate match.
    /// </summary>
    /// <param name="target">The target TrackInfo from MusicBrainz</param>
    /// <param name="candidate">The candidate source (e.g., yt-dlp result)</param>
    /// <returns>ScoreResult containing the score and recommendation</returns>
    ScoreResult Score(TrackInfo target, SourceCandidate candidate);
}