namespace MusicSync.Core.Interfaces;

/// <summary>
/// Fetches front cover art for a release group from the Cover Art Archive.
/// Returns null when no cover exists or the archive cannot be reached.
/// </summary>
public interface ICoverArtProvider
{
    Task<byte[]?> GetFrontCoverAsync(Guid releaseGroupMbid, CancellationToken ct);
}
