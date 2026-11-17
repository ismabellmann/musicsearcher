using Microsoft.EntityFrameworkCore;

namespace MusicSync.Infrastructure;

public class MusicSyncDbContext : DbContext
{
    public MusicSyncDbContext() { }

    public MusicSyncDbContext(DbContextOptions<MusicSyncDbContext> options)
        : base(options)
    { }

    public DbSet<DownloadJob> DownloadJobs { get; set; } = null!;
    public DbSet<Track> Tracks { get; set; } = null!;
    public DbSet<Album> Albums { get; set; } = null!;
    public DbSet<Artist> Artists { get; set; } = null!;
    public DbSet<TrackArtist> TrackArtists { get; set; } = null!;
    public DbSet<DownloadJobCandidate> DownloadJobCandidates { get; set; } = null!;
    public DbSet<LibraryImport> LibraryImports { get; set; } = null!;
    public DbSet<LibraryImportCandidate> LibraryImportCandidates { get; set; } = null!;
    public DbSet<LibraryFile> LibraryFiles { get; set; } = null!;
    public DbSet<Setting> Settings { get; set; } = null!;
    public DbSet<UserAccount> UserAccounts { get; set; } = null!;
    public DbSet<PlaylistImport> PlaylistImports { get; set; } = null!;
    public DbSet<PlaylistImportItem> PlaylistImportItems { get; set; } = null!;
    public DbSet<DownloadJobEvent> DownloadJobEvents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TrackArtist>(entity =>
        {
            entity.HasKey(item => new { item.TrackId, item.Position });
            entity.HasIndex(item => item.ArtistId);
            entity.HasOne(item => item.Track)
                .WithMany(track => track.TrackArtists)
                .HasForeignKey(item => item.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Artist)
                .WithMany()
                .HasForeignKey(item => item.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlaylistImport>(entity =>
        {
            entity.HasIndex(item => item.ExternalId);
            entity.HasMany(item => item.Items)
                .WithOne(item => item.PlaylistImport)
                .HasForeignKey(item => item.PlaylistImportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaylistImportItem>(entity =>
        {
            entity.HasIndex(item => new { item.PlaylistImportId, item.Position }).IsUnique();
        });

        modelBuilder.Entity<DownloadJobEvent>(entity =>
        {
            entity.HasIndex(item => new { item.JobId, item.Id });
            entity.HasIndex(item => item.CreatedAt);
        });

        modelBuilder.Entity<DownloadJobCandidate>(entity =>
        {
            entity.HasIndex(item => new { item.DownloadJobId, item.Rank }).IsUnique();
            entity.HasOne(item => item.DownloadJob)
                .WithMany(job => job.Candidates)
                .HasForeignKey(item => item.DownloadJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryImport>(entity =>
        {
            entity.HasIndex(item => item.RelativePath).IsUnique();
            entity.HasIndex(item => item.Status);
        });

        modelBuilder.Entity<LibraryImportCandidate>(entity =>
        {
            entity.HasIndex(item => new { item.LibraryImportId, item.Rank }).IsUnique();
            entity.HasOne(item => item.LibraryImport)
                .WithMany(import => import.Candidates)
                .HasForeignKey(item => item.LibraryImportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasIndex(item => item.Mbid)
                .IsUnique()
                .HasFilter("\"Mbid\" IS NOT NULL");
            entity.HasIndex(item => item.Name);
        });
    }
}
