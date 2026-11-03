using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

/// <summary>
/// One scored source candidate kept for review. Rank 1 is the best-scoring candidate.
/// </summary>
public class DownloadJobCandidate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int DownloadJobId { get; set; }

    public DownloadJob DownloadJob { get; set; } = null!;

    [Required]
    public int Rank { get; set; }

    [Required]
    [MaxLength(500)]
    public string SourceUrl { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public int? DurationMs { get; set; }

    [Column(TypeName = "real")]
    public float Score { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}
