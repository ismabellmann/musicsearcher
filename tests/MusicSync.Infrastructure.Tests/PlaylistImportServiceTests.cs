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
    private FakeMetadataProvider _metadata = null!;
    private FakePopularityProvider _popularity = null!;
    private MusicSyncDbContext _db = null!;
    private PlaylistImportService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _metadata = new FakeMetadataProvider();
        _popularity = new FakePopularityProvider();
        _service = new PlaylistImportService(
            _db,
            new FakePlaylistSource(),
            _metadata,
            new TrackScorer(),
            new PlaylistOptions(),
            _popularity);
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

    [Fact]
    public async Task PreviewAlbum_UsesAlbumRecordingsAndCanEnqueueThem()
    {
        var albumId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _metadata.Album = new AlbumDetail
        {
            Mbid = albumId,
            Title = "Smoke Album",
            ArtistNames = new[] { "Smoke Artist" },
            Tracks = new[]
            {
                new TrackInfo
                {
                    RecordingMbid = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Title = "Album Song",
                    ArtistName = "Smoke Artist",
                    Artists = new[] { new ArtistCredit { Name = "Smoke Artist", Mbid = Guid.NewGuid() } },
                    AlbumTitle = "Smoke Album",
                    DurationMs = 200000
                }
            }
        };

        var preview = await _service.PreviewAlbumAsync(albumId, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("Smoke Album", preview!.Name);
        Assert.Equal(PlaylistImportItemStatus.Matched, Assert.Single(preview.Items).Status);
        var result = await _service.EnqueueAsync(preview.ImportId, Array.Empty<int>(), null, CancellationToken.None);
        Assert.Single(result!.Enqueued);
    }

    [Fact]
    public async Task PreviewArtist_MapsListenBrainzRecordingsToMusicBrainz()
    {
        var artistId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var recordingId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        _popularity.Recordings = new[] { new RankedRecording { RecordingMbid = recordingId, Title = "Top Song", Position = 1 } };
        _metadata.SearchResult = new TrackInfo
        {
            RecordingMbid = recordingId,
            Title = "Top Song",
            ArtistName = "Top Artist",
            Artists = new[] { new ArtistCredit { Name = "Top Artist", Mbid = artistId } }
        };

        var preview = await _service.PreviewArtistAsync(artistId, "Top Artist", 1, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal(PlaylistImportItemStatus.Matched, Assert.Single(preview!.Items).Status);
        Assert.Equal(recordingId, preview.Items[0].SelectedRecordingMbid);
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
        public AlbumDetail? Album { get; set; }
        public TrackInfo? SearchResult { get; set; }
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
            Task.FromResult<IReadOnlyList<TrackInfo>>(SearchResult is null ? new[] { Candidate } : new[] { SearchResult });

        public Task<IReadOnlyList<TrackInfo>> SearchAlbumsAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrackInfo>>(Array.Empty<TrackInfo>());

        public Task<IReadOnlyList<TrackInfo>> SearchArtistsAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrackInfo>>(Array.Empty<TrackInfo>());

        public Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct) =>
            Task.FromResult(Album);

        public Task<TrackInfo?> LookupByIsrcAsync(string isrc, CancellationToken ct) =>
            Task.FromResult<TrackInfo?>(null);

        public Task<TrackInfo?> GetRecordingAsync(Guid recordingMbid, CancellationToken ct) =>
            Task.FromResult<TrackInfo?>(null);
    }

    private sealed class FakePopularityProvider : IPopularityProvider
    {
        public IReadOnlyList<RankedRecording> Recordings { get; set; } = Array.Empty<RankedRecording>();

        public Task<IReadOnlyList<RankedRecording>> TopRecordingsForArtistAsync(Guid artistMbid, int count, CancellationToken ct) =>
            Task.FromResult(Recordings.Take(count).ToList() as IReadOnlyList<RankedRecording>);
    }
}
