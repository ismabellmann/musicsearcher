using MusicSync.Core;

namespace MusicSync.Worker;

/// <summary>
/// Deterministic provider used by the first persistent pipeline slice.
/// It produces a scored candidate without network or executable dependencies.
/// </summary>
public sealed class DeterministicAcquisitionProvider : IAcquisitionProvider
{
    public string Name => "deterministic";

    public Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(
        TrackInfo track,
        int max,
        CancellationToken ct)
    {
        IReadOnlyList<SourceCandidate> candidates = string.IsNullOrWhiteSpace(track.Title)
            ? Array.Empty<SourceCandidate>()
            : new[]
            {
                new SourceCandidate
                {
                    Title = track.Title,
                    SourceUrl = $"deterministic://{track.RecordingMbid}",
                    Format = ".mp3",
                    DurationMs = track.DurationMs
                }
            };

        return Task.FromResult<IReadOnlyList<SourceCandidate>>(candidates.Take(Math.Max(0, max)).ToList());
    }

    public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
    {
        return Task.FromResult(new DownloadedFile { FilePath = string.Empty });
    }
}
