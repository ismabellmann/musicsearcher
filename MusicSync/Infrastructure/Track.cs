using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public class Track
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid RecordingMbid { get; set; }

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Display credit built from TrackArtists (for example "A & B").</summary>
    [MaxLength(200)]
    public string ArtistName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AlbumTitle { get; set; }

    /// <summary>MusicBrainz release group used to fetch cover art.</summary>
    public Guid? ReleaseGroupMbid { get; set; }

    public int? Year { get; set; }

    [MaxLength(20)]
    public string? Isrc { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Origin of a track that has no MusicBrainz recording, for example a YouTube video URL.</summary>
    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    public ICollection<TrackArtist> TrackArtists { get; set; } = new List<TrackArtist>();
}