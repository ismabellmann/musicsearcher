using MusicSync.Core;

namespace MusicSync.Core.Interfaces;

/// <summary>
/// Writes canonical tags and optional embedded cover art into an audio file.
/// </summary>
public interface ITagWriter
{
    Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct);

    /// <summary>
    /// Replaces only the embedded front cover, leaving existing text tags untouched.
    /// Used when importing files that already belong to the library.
    /// </summary>
    Task WriteCoverAsync(string filePath, byte[] coverArt, CancellationToken ct);
}
