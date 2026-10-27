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
    public DbSet<LibraryFile> LibraryFiles { get; set; } = null!;
    public DbSet<Setting> Settings { get; set; } = null!;
    public DbSet<UserAccount> UserAccounts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
