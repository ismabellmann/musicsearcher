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
    public DbSet<LibraryFile> LibraryFiles { get; set; } = null!;
    public DbSet<Setting> Settings { get; set; } = null!;
    public DbSet<UserAccount> UserAccounts { get; set; } = null!;

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

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasIndex(item => item.Mbid)
                .IsUnique()
                .HasFilter("\"Mbid\" IS NOT NULL");
            entity.HasIndex(item => item.Name);
        });
    }
}
