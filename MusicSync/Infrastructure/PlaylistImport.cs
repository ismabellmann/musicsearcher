using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicSync.Infrastructure;

public sealed class PlaylistImport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string SourceUrl { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? OwnerName { get; set; }

    public int TotalTracks { get; set; }

    [Required, MaxLength(30)]
    public string Status { get; set; } = PlaylistImportStatus.Previewed;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PlaylistImportItem> Items { get; set; } = new List<PlaylistImportItem>();
}

public sealed class PlaylistImportItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int PlaylistImportId { get; set; }
    public PlaylistImport PlaylistImport { get; set; } = null!;

    public int Position { get; set; }

    [Required, MaxLength(200)]
    public string ExternalTrackId { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ArtistName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AlbumName { get; set; }

    public int DurationMs { get; set; }

    [Required, MaxLength(30)]
    public string Status { get; set; } = PlaylistImportItemStatus.NoMatch;

    public Guid? SelectedRecordingMbid { get; set; }
    public int? TrackId { get; set; }
    public int? DownloadJobId { get; set; }

    [Required]
    public string CandidatesJson { get; set; } = "[]";
}

public static class PlaylistImportStatus
{
    public const string Previewed = "Previewed";
    public const string Enqueued = "Enqueued";
}

public static class PlaylistImportItemStatus
{
    public const string Matched = "Matched";
    public const string NeedsReview = "NeedsReview";
    public const string NoMatch = "NoMatch";
    public const string Duplicate = "Duplicate";
    public const string Enqueued = "Enqueued";
    public const string Failed = "Failed";
}
