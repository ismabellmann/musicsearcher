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

    [MaxLength(200)]
    public string ArtistName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AlbumTitle { get; set; }

    [MaxLength(20)]
    public string? Isrc { get; set; }

    public int DurationMs { get; set; }
}