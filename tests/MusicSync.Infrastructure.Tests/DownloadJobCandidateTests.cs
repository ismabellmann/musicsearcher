using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class DownloadJobCandidateTests
{
    private static async Task<(SqliteConnection Connection, MusicSyncDbContext Db)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options;
        var db = new MusicSyncDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static async Task<DownloadJob> AddJobAsync(MusicSyncDbContext db)
    {
        var track = new Track { RecordingMbid = Guid.NewGuid(), Title = "Song", ArtistName = "Artist", DurationMs = 1000 };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var job = new DownloadJob { TrackId = track.Id, Status = DownloadJobStatus.NeedsReview, ProviderName = "yt-dlp" };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Candidates_PersistInRankOrder_AndCascadeWhenJobIsDeleted()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;

        var job = await AddJobAsync(db);
        db.DownloadJobCandidates.AddRange(
            new DownloadJobCandidate { DownloadJobId = job.Id, Rank = 2, SourceUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb", Title = "Second", Score = 0.7f },
            new DownloadJobCandidate { DownloadJobId = job.Id, Rank = 1, SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa", Title = "First", DurationMs = 95000, Score = 0.9f, Reason = "duration:0.40; title:0.30" });
        await db.SaveChangesAsync();

        var ordered = await db.DownloadJobCandidates
            .Where(item => item.DownloadJobId == job.Id)
            .OrderBy(item => item.Rank)
            .ToListAsync();
        Assert.Equal(new[] { "First", "Second" }, ordered.Select(item => item.Title));
        Assert.Equal(95000, ordered[0].DurationMs);
        Assert.Null(ordered[1].DurationMs);

        db.DownloadJobs.Remove(job);
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.DownloadJobCandidates.CountAsync());
    }

    [Fact]
    public async Task DuplicateRank_ForSameJob_IsRejected()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;

        var job = await AddJobAsync(db);
        db.DownloadJobCandidates.Add(new DownloadJobCandidate { DownloadJobId = job.Id, Rank = 1, SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa", Title = "A" });
        await db.SaveChangesAsync();

        db.DownloadJobCandidates.Add(new DownloadJobCandidate { DownloadJobId = job.Id, Rank = 1, SourceUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb", Title = "B" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
