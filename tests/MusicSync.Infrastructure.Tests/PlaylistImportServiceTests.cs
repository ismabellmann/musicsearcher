using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class PlaylistImportServiceTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MusicSyncDbContext _db = null!;
    private PlaylistImportService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new PlaylistImportService(
            _db,
            new FakePlaylistSource(),
            new FakeMetadataProvider(),
            new TrackScorer(),
            new PlaylistOptions());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose() { }

    [Fact]
    public async Task Preview_PersistsMatchedItemsAndIsIdempotent()
    {
        var first = await _service.PreviewAsync("https://open.spotify.com/playlist/demo", CancellationToken.None);
        var second = await _service.PreviewAsync("https://open.spotify.com/playlist/demo", CancellationToken.None);

        Assert.Equal(first.ImportId, second.ImportId);
        Assert.Single(first.Items);
        Assert.Equal(PlaylistImportItemStatus.Matched, first.Items[0].Status);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), first.Items[0].SelectedRecordingMbid);
        Assert.Equal(1, await _db.PlaylistImports.CountAsync());
        Assert.Equal(1, await _db.PlaylistImportItems.CountAsync());
    }

    [Fact]
    public async Task Enqueue_IsIdempotentAndCreatesOneJob()
    {
        var preview = await _service.PreviewAsync("https://open.spotify.com/playlist/demo", CancellationToken.None);

        var first = await _service.EnqueueAsync(
            preview.ImportId,
            new[] { preview.Items[0].ItemId },
            null,
            CancellationToken.None);
        var second = await _service.EnqueueAsync(
            preview.ImportId,
            new[] { preview.Items[0].ItemId },
            null,
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Single(first!.Enqueued);
        Assert.Empty(first.Skipped);
        Assert.NotNull(second);
        Assert.Empty(second!.Enqueued);
        Assert.Equal("AlreadyEnqueued", Assert.Single(second.Skipped).Reason);
        Assert.Equal(1, await _db.DownloadJobs.CountAsync());
    }

    private sealed class FakePlaylistSource : IPlaylistSource
    {
        public Task<PlaylistInfo> GetPlaylistAsync(string urlOrId, CancellationToken ct) =>
            Task.FromResult(new PlaylistInfo
            {
                Id = "demo",
                Name = "Demo",
                TotalTracks = 1,
                Tracks = new[]
                {
                    new PlaylistTrack
                    {
                        Id = "spotify-track-1",
                        Name = "Smoke Song",
                        ArtistName = "Smoke Artist",
                        AlbumName = "Smoke Album",
                        DurationMs = 180000
                    }
                }
            });
    }

    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        private static readonly TrackInfo Candidate = new()
        {
            RecordingMbid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "Smoke Song",
            ArtistName = "Smoke Artist",
            Artists = new[] { new ArtistCredit { Name = "Smoke Artist", Mbid = Guid.NewGuid() } },
            AlbumTitle = "Smoke Album",
            DurationMs = 180000
        };

        public Task<IReadOnlyList<TrackInfo>> SearchTracksAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrackInfo>>(new[] { Candidate });

        public Task<IReadOnlyList<TrackInfo>> SearchAlbumsAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrackInfo>>(Array.Empty<TrackInfo>());

        public Task<IReadOnlyList<TrackInfo>> SearchArtistsAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrackInfo>>(Array.Empty<TrackInfo>());

        public Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct) =>
            Task.FromResult<AlbumDetail?>(null);

        public Task<TrackInfo?> LookupByIsrcAsync(string isrc, CancellationToken ct) =>
            Task.FromResult<TrackInfo?>(null);
    }
}
