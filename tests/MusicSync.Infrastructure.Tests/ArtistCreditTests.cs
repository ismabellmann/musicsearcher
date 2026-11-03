using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class ArtistCreditTests
{
    [Fact]
    public async Task GetOrCreateTrack_StoresOrderedCreditsReusesArtistsAndFindsSecondaryArtists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options;
        await using var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var poldMbid = Guid.NewGuid();
        var track = await TrackCatalog.GetOrCreateTrackAsync(db, new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Hideout",
            DurationMs = 95000,
            Artists = new[] { new ArtistCredit("Pold", poldMbid), new ArtistCredit("Guest Singer", Guid.Empty) }
        }, CancellationToken.None);

        await TrackCatalog.GetOrCreateTrackAsync(db, new TrackInfo
        {
            RecordingMbid = Guid.NewGuid(),
            Title = "Another Song",
            DurationMs = 1000,
            Artists = new[] { new ArtistCredit("Pold", poldMbid) }
        }, CancellationToken.None);

        Assert.Equal("Pold & Guest Singer", track.ArtistName);
        Assert.Equal(1, await db.Artists.CountAsync(item => item.Mbid == poldMbid));
        Assert.Equal(2, await db.Artists.CountAsync());

        var loaded = await TrackCatalog.FindWithArtistsAsync(db, track.Id, CancellationToken.None);
        var info = TrackCatalog.ToTrackInfo(loaded!);
        Assert.Equal(new[] { "Pold", "Guest Singer" }, info.Artists.Select(credit => credit.Name));
        Assert.Equal("Pold", info.GetPrimaryArtistName());

        var trackIdsForGuest = await db.TrackArtists
            .Where(link => link.Artist.Name == "Guest Singer")
            .Select(link => link.TrackId)
            .ToListAsync();
        Assert.Equal(new[] { track.Id }, trackIdsForGuest);
    }

    [Fact]
    public async Task LegacyDatabase_IsBaselinedAndPrimaryArtistIsBackfilled()
    {
        var path = Path.Combine(Path.GetTempPath(), "musicsync-migrate-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new MusicSyncDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261029222411_InitialCreate");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Tracks\" (\"RecordingMbid\", \"Title\", \"ArtistName\", \"DurationMs\") " +
                    $"VALUES ('{Guid.NewGuid()}', 'Old Song', 'Old Artist', 1000);");
                await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
            }

            await using (var db = new MusicSyncDbContext(options))
            {
                await MusicSyncDatabaseMigrator.MigrateAsync(db);

                var link = await db.TrackArtists.Include(item => item.Artist).SingleAsync();
                Assert.Equal("Old Artist", link.Artist.Name);
                Assert.Equal(0, link.Position);
                Assert.Null(link.Artist.Mbid);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
