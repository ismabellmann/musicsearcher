namespace MusicSync.Core;

/// <summary>
/// An audio source candidate returned by an acquisition provider.
/// </summary>
public record SourceCandidate
{
    public string Title { get; init; } = string.Empty;
    /// <summary>Artist credit of the candidate, when known separately from its title.</summary>
    public string? ArtistName { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string Format { get; init; } = ".mp3";
    public int? DurationMs { get; init; }
    public byte[]? CoverArtBytes { get; init; }
    public string FilePath { get; init; } = string.Empty;
}
