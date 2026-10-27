using MusicSync.Core.Interfaces;

namespace MusicSync.Core;

/// <summary>
/// Resolves library file paths based on the track metadata and configuration.
/// Handles multiple artists properly to avoid the "composite artist C" bug.
/// </summary>
public class LibraryPathResolver : ILibraryPathResolver
{
    private const string LibraryRootPlaceholder = "{LibraryRoot}";

    /// <summary>
    /// Resolves the library path for a track.
    /// </summary>
    /// <param name="track">The track metadata with multi-artist support.</param>
    /// <param name="asSingle">If true, resolves as a single (no album folder).</param>
    /// <param name="extension">The file extension (including dot, e.g. ".mp3").</param>
    /// <returns>The resolved library path.</returns>
    public string Resolve(TrackInfo track, bool asSingle, string extension)
    {
        // Sanitize components to prevent path traversal (section 11 security requirement)
        var primaryArtist = SanitizeFileName(track.GetPrimaryArtistName() ?? "UnknownArtist");
        var title = SanitizeFileName(track.Title);
        var trackNumber = track.TrackNumber ?? 0;

        // Determine the artist string to use in the path
        // Use primary artist only to avoid composite artist issues
        // The track will be indexed under the primary artist, but MBIDs link to all artists
        var artistString = primaryArtist;

        if (asSingle || track.IsSingle)
        {
            // Single track format: {LibraryRoot}/{Artist}/{Title}.{ext}
            // Use primary artist to avoid the "A+B=C" composite artist bug
            return $"{LibraryRootPlaceholder}/{{artist}}/{{title}}{{extension}}";
        }

        // Album track format: {LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}
        var album = SanitizeFileName(track.AlbumTitle ?? "UnknownAlbum");
        var nn = trackNumber > 0 ? $"{trackNumber:D2}" : "00";

        return $"{LibraryRootPlaceholder}/{{artist}}/{{album}}/{{nn}} - {{title}}{{extension}}";
    }

    /// <summary>
    /// Sanitizes a string to be safe for use in file names.
    /// Prevents path traversal attacks per section 11 requirements.
    /// </summary>
    /// <param name="value">The string to sanitize.</param>
    /// <returns>A sanitized string safe for file names.</returns>
    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "Unknown";

        // Remove or replace characters that are invalid in file names
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var result = string.Join("_", value.Split(invalidChars));
        // Trim multiple underscores
        while (result.Contains("__"))
            result = result.Replace("__", "_");
        return result.Trim('_');
    }
}