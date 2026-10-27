using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public class DownloadJob
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int TrackId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Queued";

    [Required]
    public int Attempts { get; set; } = 0;

    [MaxLength(500)]
    public string? Error { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    [MaxLength(100)]
    public string? ProviderName { get; set; }

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [Column(TypeName = "real")]
    public float? MatchScore { get; set; }

    [MaxLength(500)]
    public string? OutputPath { get; set; }
}