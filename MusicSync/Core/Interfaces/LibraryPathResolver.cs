namespace MusicSync.Core.Interfaces;

/// <summary>
/// Resolves library file paths based on track metadata and configuration.
/// </summary>
public interface ILibraryPathResolver
{
    /// <summary>
    /// Resolves the library path for a track.
    /// </summary>
    /// <param name="track">The track metadata.</param>
    /// <param name="asSingle">If true, resolves as a single (no album folder).</param>
    /// <param name="extension">The file extension (including dot, e.g. ".mp3").</param>
    /// <returns>The resolved library path pattern.</returns>
    string Resolve(TrackInfo track, bool asSingle, string extension);
}