namespace MusicSync.Core.Interfaces;

/// <summary>
/// Searches video sources for a track's metadata. Search results only: nothing is downloaded.
/// </summary>
public interface IVideoSearchProvider
{
    Task<IReadOnlyList<SourceCandidate>> SearchAsync(TrackInfo target, int max, CancellationToken ct);
}
