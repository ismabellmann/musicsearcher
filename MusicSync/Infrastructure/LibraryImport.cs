using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

/// <summary>
/// A file found in the library folder that is waiting to be matched to MusicBrainz or YouTube.
/// </summary>
public class LibraryImport
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ArtistName { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AlbumTitle { get; set; }

    public int? DurationMs { get; set; }

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = LibraryImportStatus.Pending;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? IdentifiedAt { get; set; }

    public int? TrackId { get; set; }

    /// <summary>Where the accepted match came from: a MusicBrainz recording or a YouTube video.</summary>
    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    public ICollection<LibraryImportCandidate> Candidates { get; set; } = new List<LibraryImportCandidate>();
}

public static class LibraryImportProvider
{
    public const string MusicBrainz = "musicbrainz";
    public const string YouTube = "youtube";
}

public static class LibraryImportStatus
{
    public const string Pending = "Pending";
    public const string Identified = "Identified";
    public const string NoMatch = "NoMatch";
    public const string Imported = "Imported";
    public const string Rejected = "Rejected";
}

/// <summary>
/// One recording or video proposed for a library file. Rank 1 scored best.
/// </summary>
public class LibraryImportCandidate
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int LibraryImportId { get; set; }

    public LibraryImport LibraryImport { get; set; } = null!;

    [Required]
    public int Rank { get; set; }

    [Required]
    public Guid RecordingMbid { get; set; }

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string ArtistsJson { get; set; } = "[]";

    [MaxLength(500)]
    public string? AlbumTitle { get; set; }

    /// <summary>MusicBrainz release group used to fetch cover art.</summary>
    public Guid? ReleaseGroupMbid { get; set; }

    public int? Year { get; set; }

    public int? DurationMs { get; set; }

    [MaxLength(20)]
    public string? Isrc { get; set; }

    [Column(TypeName = "real")]
    public float Score { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    [Required]
    [MaxLength(20)]
    public string Provider { get; set; } = LibraryImportProvider.MusicBrainz;

    [Required]
    [MaxLength(500)]
    public string SourceUrl { get; set; } = string.Empty;
}
