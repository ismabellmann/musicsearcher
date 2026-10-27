using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public class LibraryFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int TrackId { get; set; }

    [Required]
    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;

    public string? Format { get; set; }

    public int? Bitrate { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [Required]
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}