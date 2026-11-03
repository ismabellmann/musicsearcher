using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public class Artist
{
    [Key]
    public int Id { get; set; }

    public Guid? Mbid { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;
}