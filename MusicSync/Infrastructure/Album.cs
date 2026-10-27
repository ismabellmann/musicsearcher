using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public class Album
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid Mbid { get; set; }

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }
}