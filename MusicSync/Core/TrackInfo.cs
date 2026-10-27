using System.Text.Json.Serialization;

namespace MusicSync.Core;

/// <summary>
/// Domain model representing a track with metadata needed for library path resolution.
/// Supports multiple artists via MBIDs and names, avoiding the "composite artist C" bug.
/// </summary>
public record TrackInfo
{
    /// <summary>Gets or sets the MusicBrainz recording ID.</summary>
    public Guid RecordingMbid { get; init; }

    /// <summary>Gets or sets the ISRC code, if available.</summary>
    public string? Isrc { get; init; }

    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary artist name. For tracks with multiple artists,
    /// use the Artists collection instead. The primary artist is the first listed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtistName { get; init; }

    /// <summary>
    /// Gets or sets the collection of performing artists with their MBIDs.
    /// This avoids the "composite artist C" bug where a track with artists A and B
    /// would create a new artist "A+B" instead of appearing in both A and B lists.
    /// </summary>
    public IReadOnlyList<ArtistCredit> Artists { get; init; } = System.Array.Empty<ArtistCredit>();

    /// <summary>
    /// Gets or sets the album title, if applicable.
    /// </summary>
    public string? AlbumTitle { get; init; }

    /// <summary>Gets or sets the track number on the album.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Gets or sets the disc number.</summary>
    public int? DiscNumber { get; init; }

    /// <summary>Gets or sets the duration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>
    /// Gets a value indicating whether this track is a single (no album).
    /// Determined by checking if AlbumTitle is null/empty AND Artists count is small.
    /// </summary>
    public bool IsSingle => string.IsNullOrEmpty(AlbumTitle) && Artists.Count <= 2;

    /// <summary>
    /// Gets the primary artist name (first artist in the Artists collection, or ArtistName fallback).
    /// </summary>
    public string? GetPrimaryArtistName()
    {
        return Artists.Count > 0 ? Artists[0].Name : ArtistName;
    }

    /// <summary>
    /// Gets all artist names separated by "&" for display/path purposes.
    /// </summary>
    public string GetArtistNamesJoined(string separator = " & ")
    {
        return string.Join(separator, Artists.Select(a => a.Name));
    }

    /// <summary>
    /// Gets the primary artist MBID (first artist in the collection).
    /// </summary>
    public Guid? GetPrimaryArtistMbid()
    {
        return Artists.Count > 0 ? Artists[0].Mbid : null;
    }
}

/// <summary>
/// Represents a single artist credit with their MusicBrainz ID.
/// Used to properly attribute tracks to actual artists instead of creating composite artists.
/// </summary>
public record ArtistCredit
{
    /// <summary>Gets or sets the artist name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the MusicBrainz artist ID.</summary>
    public Guid Mbid { get; init; }

    /// <summary>
    /// Creates an ArtistCredit from a MusicBrainz artist relationship.
    /// </summary>
    /// <param name="name">Artist name</param>
    /// <param name="mbid">MusicBrainz artist MBID</param>
    public ArtistCredit(string name, Guid mbid)
    {
        Name = name;
        Mbid = mbid;
    }

    /// <summary>
    /// Default constructor for serialization.
    /// </summary>
    public ArtistCredit() { }
}