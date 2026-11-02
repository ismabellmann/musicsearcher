using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MusicSync.Infrastructure;

public sealed class MusicSyncDbContextFactory : IDesignTimeDbContextFactory<MusicSyncDbContext>
{
    public MusicSyncDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>()
            .UseSqlite("Data Source=musicsync-design.db")
            .Options;

        return new MusicSyncDbContext(options);
    }
}
