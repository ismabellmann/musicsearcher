using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public sealed class DownloadJobEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public int JobId { get; set; }

    [Required, MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Status { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
