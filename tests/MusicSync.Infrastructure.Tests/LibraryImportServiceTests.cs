using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class LibraryImportServiceTests : IAsyncLifetime, IDisposable
{
    private static readonly Guid HideoutMbid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherMbid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid HideoutReleaseGroupMbid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "musicsync-import-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeVideoProvider _video = new();
    private readonly FakeCoverArtProvider _coverArt = new();
    private readonly RecordingTagWriter _tagWriter = new();
    private readonly LibraryOptions _options = new();
    private MusicSyncDbContext _db = null!;
    private LibraryImportService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        Directory.CreateDirectory(Path.Combine(_root, "Pold"));
        Directory.CreateDirectory(Path.Combine(_root, "Band", "Album"));
        Directory.CreateDirectory(Path.Combine(_root, "Deep", "Too", "Far"));
        File.WriteAllText(Path.Combine(_root, "Pold", "Hideout.mp3"), "not real audio hideout");
        File.WriteAllText(Path.Combine(_root, "Pold", "Unknown Song.mp3"), "not real audio unknown");
        File.WriteAllText(Path.Combine(_root, "Band", "Album", "03 - Tune.flac"), "not real audio tune");
        File.WriteAllText(Path.Combine(_root, "stray.mp3"), "top level");
        File.WriteAllText(Path.Combine(_root, "Deep", "Too", "Far", "x.mp3"), "too deep");
        File.WriteAllText(Path.Combine(_root, "Pold", "notes.txt"), "not audio");

        _options.Root = _root;
        _options.StagingDirectory = Path.Combine(_root, "..", "staging");
        _service = new LibraryImportService(
            _db,
            new FakeMetadata(),
            _video,
            new TrackScorer(),
            new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions()),
            _options,
            _coverArt,
            _tagWriter);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Scan_RecognizesArtistTitleAndArtistAlbumLayouts_AndSkipsTheRest()
    {
        var summary = await _service.ScanAsync(CancellationToken.None);

        Assert.Equal(3, summary.Added);
        Assert.Equal(2, summary.Unrecognized);

        var imports = await _db.LibraryImports.OrderBy(item => item.RelativePath).ToListAsync();
        Assert.Equal(new[] { "Band/Album/03 - Tune.flac", "Pold/Hideout.mp3", "Pold/Unknown Song.mp3" }, imports.Select(item => item.RelativePath));

        var tune = imports[0];
        Assert.Equal("Band", tune.ArtistName);
        Assert.Equal("Album", tune.AlbumTitle);
        Assert.Equal("Tune", tune.Title);
        Assert.Equal("Pold", imports[1].ArtistName);
        Assert.Equal("Hideout", imports[1].Title);
    }

    [Fact]
    public async Task Scan_IsIdempotent()
    {
        await _service.ScanAsync(CancellationToken.None);
        var second = await _service.ScanAsync(CancellationToken.None);

        Assert.Equal(0, second.Added);
        Assert.Equal(3, second.AlreadyKnown);
    }

    [Fact]
    public async Task Scan_SkipsStagingDirectoryInsideTheLibrary()
    {
        var staging = Path.Combine(_root, ".musicsync-staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "12.mp3"), "staged audio");
        _options.StagingDirectory = staging;

        var summary = await _service.ScanAsync(CancellationToken.None);

        Assert.Equal(3, summary.Added);
        Assert.Equal(2, summary.Unrecognized);
        Assert.DoesNotContain(
            await _db.LibraryImports.Select(item => item.RelativePath).ToListAsync(),
            path => path.StartsWith(".musicsync-staging/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identify_StoresRankedCandidates_WithoutChangingTheLibrary()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");

        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);

        var detail = await _db.LibraryImports.Include(item => item.Candidates).SingleAsync(item => item.Id == hideout.Id);
        Assert.Equal(LibraryImportStatus.Identified, detail.Status);
        Assert.Equal(new[] { HideoutMbid, OtherMbid }, detail.Candidates.OrderBy(item => item.Rank).Select(item => item.RecordingMbid));
        Assert.Equal(0, await _db.Tracks.CountAsync());
        Assert.Equal(0, await _db.LibraryFiles.CountAsync());
    }

    [Fact]
    public async Task Identify_MarksNoMatch_WhenOnlyOtherArtistsMatchTheTitle()
    {
        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");

        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);

        var detail = await _db.LibraryImports.SingleAsync(item => item.Id == unknown.Id);
        Assert.Equal(LibraryImportStatus.NoMatch, detail.Status);
        Assert.NotEmpty(await _db.LibraryImportCandidates.Where(item => item.LibraryImportId == unknown.Id).ToListAsync());
    }

    [Fact]
    public async Task Accept_CreatesTrackArtistsAndLibraryFile()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(item => item.LibraryImportId == hideout.Id && item.Rank == 1);

        var outcome = await _service.AcceptCandidateAsync(hideout.Id, candidate.Id, CancellationToken.None);

        Assert.Equal(AcceptOutcome.Imported, outcome);
        var track = await _db.Tracks.Include(item => item.TrackArtists).ThenInclude(link => link.Artist).SingleAsync();
        Assert.Equal(HideoutMbid, track.RecordingMbid);
        Assert.Equal("Pold & Guest", track.ArtistName);
        var file = await _db.LibraryFiles.SingleAsync();
        Assert.Equal("Pold/Hideout.mp3", file.RelativePath);
        Assert.Equal(64, file.Sha256.Length);
        Assert.Equal(LibraryImportStatus.Imported, (await _db.LibraryImports.SingleAsync(item => item.Id == hideout.Id)).Status);
    }

    [Fact]
    public async Task Accept_EmbedsReleaseGroupFrontCoverIntoTrack()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(item => item.LibraryImportId == hideout.Id && item.Rank == 1);

        var outcome = await _service.AcceptCandidateAsync(hideout.Id, candidate.Id, CancellationToken.None);

        Assert.Equal(AcceptOutcome.Imported, outcome);
        Assert.Equal(HideoutReleaseGroupMbid, _coverArt.RequestedMbid);
        Assert.Equal(Path.Combine(_root, "Pold", "Hideout.mp3"), _tagWriter.CoverPath);
        Assert.Equal(FakeCoverArtProvider.CoverBytes, _tagWriter.CoverBytes);
        var track = await _db.Tracks.SingleAsync();
        Assert.Equal(HideoutReleaseGroupMbid, track.ReleaseGroupMbid);
        Assert.Equal(2020, track.Year);
    }

    [Fact]
    public async Task Accept_StillImports_WhenCoverArtIsUnavailable()
    {
        _coverArt.Cover = null;
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(item => item.LibraryImportId == hideout.Id && item.Rank == 1);

        var outcome = await _service.AcceptCandidateAsync(hideout.Id, candidate.Id, CancellationToken.None);

        Assert.Equal(AcceptOutcome.Imported, outcome);
        Assert.Null(_tagWriter.CoverPath);
    }

    [Fact]
    public async Task BackfillCovers_EmbedsCoverAndRefreshesStoredHash()
    {
        _options.EmbedCoverOnImport = false;
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(item => item.LibraryImportId == hideout.Id && item.Rank == 1);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(hideout.Id, candidate.Id, CancellationToken.None));

        var originalHash = (await _db.LibraryFiles.AsNoTracking().SingleAsync()).Sha256;
        _tagWriter.MutateFile = true;

        var summary = await _service.BackfillCoversAsync(null, CancellationToken.None);

        Assert.Equal(new CoverBackfillSummary(1, 1, 0, 0), summary);
        Assert.Equal(HideoutReleaseGroupMbid, _coverArt.RequestedMbid);
        var path = Path.Combine(_root, "Pold", "Hideout.mp3");
        var file = await _db.LibraryFiles.AsNoTracking().SingleAsync();
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        Assert.Equal(actualHash, file.Sha256);
        Assert.Equal(new FileInfo(path).Length, file.SizeBytes);
        Assert.NotEqual(originalHash, file.Sha256);
    }

    [Fact]
    public async Task BackfillCovers_SkipsMissingCoverAndFilesWithoutReleaseGroup()
    {
        _options.EmbedCoverOnImport = false;
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { hideout.Id, unknown.Id }, CancellationToken.None);
        var hideoutCandidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == hideout.Id && item.Rank == 1);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(hideout.Id, hideoutCandidate.Id, CancellationToken.None));

        var youtube = new SourceCandidate
        {
            Title = "Unknown Song",
            ArtistName = "Band",
            SourceUrl = "https://www.youtube.com/watch?v=zzzzzzzzzzz",
            DurationMs = 120000
        };
        _video.Results = new[] { youtube };
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);
        var unknownCandidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == unknown.Id && item.Provider == LibraryImportProvider.YouTube);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(unknown.Id, unknownCandidate.Id, CancellationToken.None));

        _coverArt.Cover = null;
        var summary = await _service.BackfillCoversAsync(null, CancellationToken.None);

        Assert.Equal(new CoverBackfillSummary(2, 0, 2, 0), summary);
        Assert.Null(_tagWriter.CoverPath);
    }

    [Fact]
    public async Task Accept_RejectsRecordingThatIsAlreadyInTheLibrary()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(null, CancellationToken.None);

        var first = await _db.LibraryImportCandidates.SingleAsync(item => item.LibraryImportId == hideout.Id && item.Rank == 1);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(hideout.Id, first.Id, CancellationToken.None));

        var sameRecording = await _db.LibraryImportCandidates.FirstAsync(item => item.LibraryImportId == unknown.Id && item.RecordingMbid == HideoutMbid);
        Assert.Equal(AcceptOutcome.RecordingAlreadyInLibrary, await _service.AcceptCandidateAsync(unknown.Id, sameRecording.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Accept_RejectsDuplicateContent_ForAnotherRecording()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { hideout.Id, unknown.Id }, CancellationToken.None);

        var hideoutCandidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == hideout.Id && item.Rank == 1);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(hideout.Id, hideoutCandidate.Id, CancellationToken.None));
        var firstWrites = _tagWriter.CoverWrites;

        var hideoutPath = Path.Combine(_root, "Pold", "Hideout.mp3");
        var unknownPath = Path.Combine(_root, "Pold", "Unknown Song.mp3");
        File.Copy(hideoutPath, unknownPath, overwrite: true);
        var hashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(unknownPath))).ToLowerInvariant();
        var otherCandidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == unknown.Id && item.RecordingMbid == OtherMbid);

        var outcome = await _service.AcceptCandidateAsync(unknown.Id, otherCandidate.Id, CancellationToken.None);

        Assert.Equal(AcceptOutcome.FileAlreadyInLibrary, outcome);
        Assert.Equal(1, await _db.LibraryFiles.CountAsync());
        Assert.Equal(firstWrites, _tagWriter.CoverWrites);
        var hashAfter = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(unknownPath))).ToLowerInvariant();
        Assert.Equal(hashBefore, hashAfter);
    }

    [Fact]
    public async Task Accept_RejectsCandidateFromAnotherFile()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(null, CancellationToken.None);
        var foreign = await _db.LibraryImportCandidates.FirstAsync(item => item.LibraryImportId == unknown.Id);

        Assert.Equal(AcceptOutcome.CandidateNotFound, await _service.AcceptCandidateAsync(hideout.Id, foreign.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePath_ChangesStoredPath_WithoutMovingTheFile()
    {
        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");

        Directory.CreateDirectory(Path.Combine(_root, "Manual"));
        File.WriteAllText(Path.Combine(_root, "Manual", "Hand Placed.mp3"), "not real audio");

        var outcome = await _service.UpdatePathAsync(unknown.Id, "Manual/Hand Placed.mp3", CancellationToken.None);

        Assert.Equal(AcceptOutcome.Updated, outcome);
        Assert.Equal("Manual/Hand Placed.mp3", (await _db.LibraryImports.FindAsync(unknown.Id))!.RelativePath);
        Assert.True(File.Exists(Path.Combine(_root, "Pold", "Unknown Song.mp3")));
    }

    [Fact]
    public async Task UpdatePath_RefusesAPathAnotherImportAlreadyUses()
    {
        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");

        Assert.Equal(AcceptOutcome.PathInUse, await _service.UpdatePathAsync(unknown.Id, "Pold/Hideout.mp3", CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePath_RejectsEscapingPathsAndMissingFiles()
    {
        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => _service.UpdatePathAsync(unknown.Id, "../outside.mp3", CancellationToken.None));
        Assert.Equal(AcceptOutcome.FileMissing, await _service.UpdatePathAsync(unknown.Id, "Pold/Missing.mp3", CancellationToken.None));
    }

    [Fact]
    public async Task Identify_UsesYouTubeFallback_WhenMusicBrainzArtistDoesNotMatch()
    {
        _video.Results = new[]
        {
            new SourceCandidate
            {
                Title = "Unknown Song",
                ArtistName = "Pold",
                DurationMs = 95000,
                SourceUrl = "https://www.youtube.com/watch?v=Fozjmwn3htg"
            }
        };

        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);

        var detail = await _db.LibraryImports.Include(item => item.Candidates).SingleAsync(item => item.Id == unknown.Id);
        Assert.Equal(LibraryImportStatus.Identified, detail.Status);
        Assert.Equal(1, _video.CallCount);
        var youtube = detail.Candidates.Single(item => item.Provider == LibraryImportProvider.YouTube);
        Assert.Equal("Pold", Assert.Single(System.Text.Json.JsonSerializer.Deserialize<List<ArtistCredit>>(youtube.ArtistsJson)!).Name);
        Assert.Equal("https://www.youtube.com/watch?v=Fozjmwn3htg", youtube.SourceUrl);
        Assert.Contains(detail.Candidates, item => item.Provider == LibraryImportProvider.MusicBrainz);
    }

    [Fact]
    public async Task Identify_DoesNotUseYouTube_WhenMusicBrainzMatchesTheArtist()
    {
        await _service.ScanAsync(CancellationToken.None);
        var hideout = await _db.LibraryImports.SingleAsync(item => item.Title == "Hideout");
        await _service.IdentifyAsync(new[] { hideout.Id }, CancellationToken.None);

        Assert.Equal(0, _video.CallCount);
    }

    [Fact]
    public async Task Identify_DoesNotUseYouTube_WhenTheFallbackIsDisabled()
    {
        _options.YouTubeFallback = false;

        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);

        Assert.Equal(0, _video.CallCount);
        Assert.Equal(LibraryImportStatus.NoMatch, (await _db.LibraryImports.SingleAsync(item => item.Id == unknown.Id)).Status);
    }

    [Fact]
    public async Task Identify_KeepsMusicBrainzCandidates_WhenYouTubeFails()
    {
        _video.Failure = new InvalidOperationException("yt-dlp is missing");

        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);

        var detail = await _db.LibraryImports.Include(item => item.Candidates).SingleAsync(item => item.Id == unknown.Id);
        Assert.Equal(LibraryImportStatus.NoMatch, detail.Status);
        Assert.StartsWith("YouTube:", detail.Error ?? string.Empty);
        Assert.NotEmpty(detail.Candidates);
    }

    [Fact]
    public async Task Accept_YouTubeCandidate_CreatesTrackWithStableIdAndSourceUrl()
    {
        _video.Results = new[]
        {
            new SourceCandidate
            {
                Title = "Unknown Song",
                ArtistName = "Pold",
                DurationMs = 95000,
                SourceUrl = "https://www.youtube.com/watch?v=Fozjmwn3htg"
            }
        };

        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == unknown.Id && item.Provider == LibraryImportProvider.YouTube);

        var outcome = await _service.AcceptCandidateAsync(unknown.Id, candidate.Id, CancellationToken.None);

        Assert.Equal(AcceptOutcome.Imported, outcome);
        var track = await _db.Tracks.SingleAsync();
        Assert.Equal(YouTubeIds.RecordingIdFor("Fozjmwn3htg"), track.RecordingMbid);
        Assert.Equal("https://www.youtube.com/watch?v=Fozjmwn3htg", track.SourceUrl);
        Assert.Equal("Unknown Song", track.Title);
        Assert.Equal("Pold", track.ArtistName);
        Assert.Equal("Pold/Unknown Song.mp3", (await _db.LibraryFiles.SingleAsync()).RelativePath);
        Assert.Equal("https://www.youtube.com/watch?v=Fozjmwn3htg", (await _db.LibraryImports.SingleAsync(item => item.Id == unknown.Id)).SourceUrl);
    }

    [Fact]
    public async Task Accept_YouTubeCandidateWithoutParsedArtist_KeepsFolderMetadata()
    {
        _video.Results = new[]
        {
            new SourceCandidate
            {
                Title = "OLD ROOTS [Track 9] AI-Core (Nothing Here: White Album)",
                DurationMs = 235000,
                SourceUrl = "https://www.youtube.com/watch?v=UIn-anDAGJU"
            }
        };

        await _service.ScanAsync(CancellationToken.None);
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(new[] { unknown.Id }, CancellationToken.None);
        var candidate = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == unknown.Id && item.Provider == LibraryImportProvider.YouTube);

        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(unknown.Id, candidate.Id, CancellationToken.None));

        var track = await _db.Tracks.SingleAsync();
        Assert.Equal("Unknown Song", track.Title);
        Assert.Equal("Pold", track.ArtistName);
        Assert.Equal("https://www.youtube.com/watch?v=UIn-anDAGJU", track.SourceUrl);
    }

    [Fact]
    public async Task Accept_SameYouTubeVideoTwice_IsRefused()
    {
        _video.Results = new[]
        {
            new SourceCandidate
            {
                Title = "Unknown Song",
                ArtistName = "Pold",
                DurationMs = 95000,
                SourceUrl = "https://www.youtube.com/watch?v=Fozjmwn3htg"
            }
        };

        await _service.ScanAsync(CancellationToken.None);
        var tune = await _db.LibraryImports.SingleAsync(item => item.Title == "Tune");
        var unknown = await _db.LibraryImports.SingleAsync(item => item.Title == "Unknown Song");
        await _service.IdentifyAsync(null, CancellationToken.None);

        var firstVideo = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == tune.Id && item.Provider == LibraryImportProvider.YouTube);
        Assert.Equal(AcceptOutcome.Imported, await _service.AcceptCandidateAsync(tune.Id, firstVideo.Id, CancellationToken.None));

        var sameVideo = await _db.LibraryImportCandidates.SingleAsync(
            item => item.LibraryImportId == unknown.Id && item.Provider == LibraryImportProvider.YouTube);
        Assert.Equal(AcceptOutcome.RecordingAlreadyInLibrary, await _service.AcceptCandidateAsync(unknown.Id, sameVideo.Id, CancellationToken.None));
    }

    private sealed class FakeCoverArtProvider : ICoverArtProvider
    {
        public static readonly byte[] CoverBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        public byte[]? Cover { get; set; } = CoverBytes;

        public Guid? RequestedMbid { get; private set; }

        public Task<byte[]?> GetFrontCoverAsync(Guid releaseGroupMbid, CancellationToken ct)
        {
            RequestedMbid = releaseGroupMbid;
            return Task.FromResult(Cover);
        }
    }

    private sealed class RecordingTagWriter : ITagWriter
    {
        public string? CoverPath { get; private set; }

        public byte[]? CoverBytes { get; private set; }

        public int CoverWrites { get; private set; }

        /// <summary>When true, simulates a real tag writer by changing the file bytes.</summary>
        public bool MutateFile { get; set; }

        public Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct)
            => throw new NotSupportedException("Import must not rewrite existing tags.");

        public Task WriteCoverAsync(string filePath, byte[] coverArt, CancellationToken ct)
        {
            CoverPath = filePath;
            CoverBytes = coverArt;
            CoverWrites++;
            if (MutateFile)
            {
                using var stream = File.Open(filePath, FileMode.Append);
                stream.Write(coverArt);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeVideoProvider : IVideoSearchProvider
    {
        public IReadOnlyList<SourceCandidate> Results { get; set; } = Array.Empty<SourceCandidate>();

        public Exception? Failure { get; set; }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SourceCandidate>> SearchAsync(TrackInfo target, int max, CancellationToken ct)
        {
            CallCount++;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(Results);
        }
    }

    private sealed class FakeMetadata : IMetadataProvider
    {
        public Task<IReadOnlyList<TrackInfo>> SearchTracksAsync(string query, CancellationToken ct)
        {
            IReadOnlyList<TrackInfo> results = new[]
            {
                new TrackInfo
                {
                    RecordingMbid = HideoutMbid,
                    Title = "Hideout",
                    AlbumTitle = "Hideout Album",
                    ReleaseGroupMbid = HideoutReleaseGroupMbid,
                    Year = 2020,
                    DurationMs = 95000,
                    Artists = new[] { new ArtistCredit("Pold", Guid.NewGuid()), new ArtistCredit("Guest", Guid.NewGuid()) }
                },
                new TrackInfo
                {
                    RecordingMbid = OtherMbid,
                    Title = "Something Else",
                    DurationMs = 200000,
                    Artists = new[] { new ArtistCredit("Someone", Guid.NewGuid()) }
                }
            };
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<TrackInfo>> SearchAlbumsAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackInfo>> SearchArtistsAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct) => throw new NotSupportedException();
        public Task<TrackInfo?> LookupByIsrcAsync(string isrc, CancellationToken ct) => throw new NotSupportedException();
    }
}
