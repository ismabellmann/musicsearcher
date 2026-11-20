# PROJECT_CONTEXT.md

> Context file for AI coding assistants (Claude Code, Cursor, Copilot, etc.).
> Read this fully before generating code. Treat "MUST" and "MUST NOT" as hard rules.
> Working name: **MusicSync** (rename freely).

---

## 1. Goal

A self-hosted, single-user **.NET backend** (with a minimal web UI added later) that:

1. Searches music by **song, album, artist** using **MusicBrainz** (canonical metadata), with **ListenBrainz** play counts for popularity and **Cover Art Archive** for artwork.
2. Imports track lists from **public Spotify playlists** (no user OAuth tokens).
3. For each selected track, finds an audio source, downloads and converts it, **tags it**, and saves it to a **user-configured directory** in a media-server-friendly layout.
4. Tracks jobs, history and basic **analytics** (top artists, failures, library completeness).

Reference project (design inspiration, Node/Express): https://github.com/Alextheguy1/FreeListen
FreeListen's core user value is searching MusicBrainz or public Spotify playlists and saving tagged, organized audio to a personal library, with background queue/activity, library management, and settings. This project follows that product direction in **C# / .NET**, but is currently a backend foundation rather than a feature-complete equivalent. Improvements and differences are listed in section 9.

## 2. Non-goals

- No multi-tenant SaaS, no public-internet exposure by default.
- No streaming/playback. Output is files on disk, consumed by Navidrome, Jellyfin, etc.
- No Spotify user-account integration (no per-user tokens, no private playlists).
- No DRM circumvention.

## 3. Legal / ToS note (design constraint)

Downloading audio from YouTube or SoundCloud may violate their Terms of Service and may infringe copyright depending on jurisdiction. Describing the project as personal-use software does not grant permission to download a source; users must have the necessary rights and comply with applicable law and provider terms.

Therefore:

- The acquisition may be done under fair use, that is to say, respecting the original creators intended use, so this application will be use for preservation purposes, and not for commertial use.

## 4. Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 8+ (LTS), C# with nullable reference types enabled |
| Web | ASP.NET Core minimal APIs, HTTPS only |
| Persistence | EF Core + SQLite (Postgres-compatible schema; avoid SQLite-only features) |
| Background work | `BackgroundService` polling persisted SQLite jobs in FIFO order |
| Realtime progress | Server-Sent Events (or SignalR) |
| Audio download | `yt-dlp` via `YoutubeDLSharp` or `Process` with `ArgumentList` |
| Transcoding | `ffmpeg` (external binary, required) |
| Tagging | TagLibSharp (title, artist, album, year, genre, track no., MBIDs, embedded cover) |
| HTTP clients | `IHttpClientFactory`, typed clients, Polly or built-in resilience handlers |
| Rate limiting | `System.Threading.RateLimiting` (outbound) and ASP.NET rate limiter (inbound) |
| Logging | Serilog or built-in `ILogger`, structured, no secrets |
| Tests | xUnit, FluentAssertions, WireMock.Net for external APIs |
| Packaging | Docker image that includes ffmpeg, yt-dlp **and Deno** (yt-dlp needs a JS runtime for some YouTube formats) |

## 5. Architecture

```
┌──────────┐   HTTPS    ┌───────────────────────────────────────────┐
│ Web UI / │ ─────────► │ ASP.NET Core API                          │
│ API user │ ◄───SSE─── │  Auth · Search · Playlists · Jobs · Library│
└──────────┘            └───────┬──────────────────────┬────────────┘
                                │ enqueue              │ read/write
                         ┌──────▼───────┐        ┌─────▼──────┐
                         │ Job Queue    │        │ SQLite/EF  │
                         │ (SQLite)     │        └────────────┘
                         └──────┬───────┘
                         ┌──────▼───────────────────────────────┐
                         │ Worker (BackgroundService)           │
                         │ 1. Resolve MBID / metadata           │
                         │ 2. Candidate search (provider)       │
                         │ 3. Score + pick / send to review     │
                         │ 4. Download + transcode              │
                         │ 5. Tag + embed cover                 │
                         │ 6. Move to library path + record     │
                         └──────┬───────────────────────────────┘
        ┌───────────────┬───────┴────────┬─────────────────┐
   MusicBrainz     ListenBrainz    Cover Art Archive   Spotify (public playlists)
```

### Suggested solution layout

```
src/
  MusicSync.Api/              # minimal API endpoints, auth, DI wiring, SSE
  MusicSync.Core/             # domain models, interfaces, matching logic (no I/O)
    - TrackInfo.cs            # track metadata with multi-artist support (ArtistCredit records)
    - ILibraryPathResolver.cs # interface for library path resolution
    - LibraryPathResolver.cs  # resolves paths: {LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}
  MusicSync.Infrastructure/   # EF Core, HTTP clients, yt-dlp/ffmpeg, TagLib
  MusicSync.Worker/           # BackgroundService(s) and job handlers
tests/
  MusicSync.Core.Tests/       # unit tests for core logic (scorer, path resolver, sanitization)
  MusicSync.Infrastructure.Tests/   
Dockerfile
docker-compose.yml
```

Dependency rule: `Core` depends on nothing. `Infrastructure` depends on `Core`; `Worker` depends on `Core` and `Infrastructure`. `Api` composes everything.

Current deployment: the Worker runs as a `BackgroundService` hosted inside `MusicSync.Api` (owner decision for this stage). Its code lives in the `MusicSync/Worker` project.

## 6. Key interfaces (Core)

```csharp
public interface IMetadataProvider            // MusicBrainz
{
    Task<IReadOnlyList<TrackInfo>>  SearchTracksAsync(string query, CancellationToken ct);
    Task<IReadOnlyList<AlbumInfo>>  SearchAlbumsAsync(string query, CancellationToken ct);
    Task<IReadOnlyList<ArtistInfo>> SearchArtistsAsync(string query, CancellationToken ct);
    Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct);
    Task<TrackInfo?>   LookupByIsrcAsync(string isrc, CancellationToken ct);
}

public interface IPopularityProvider          // ListenBrainz
{
    Task<IReadOnlyList<RankedRecording>> TopRecordingsForArtistAsync(Guid artistMbid, int count, CancellationToken ct);
}

public interface IPlaylistSource              // Spotify public playlists
{
    Task<PlaylistInfo> GetPlaylistAsync(string urlOrId, CancellationToken ct);
}

public interface IAcquisitionProvider         // implemented: deterministic, local-file, yt-dlp
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct);
    // Implemented signature. The worker owns the staging and work directories (§8.5);
    // DownloadedFile.IsTemporary tells the worker to delete the provider's work directory.
    Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct);
}

public interface IMatchScorer                 // pure function, heavily unit-tested
{
    MatchResult Score(TrackInfo target, SourceCandidate candidate);
}

public interface ITagWriter                   // implemented (TagLibSharp): ID3 text tags and embedded front cover (§8.5)
{
    Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct);

    // Replaces only the front cover, leaving text tags untouched. Used by library import and backfill (§8.9).
    Task WriteCoverAsync(string filePath, byte[] coverArt, CancellationToken ct);
}

public interface ILibraryPathResolver         // builds path patterns; LibraryPathResolver.ResolveWithinRoot enforces the library root
{
    string Resolve(TrackInfo track, bool asSingle, string extension);
}
```

## 7. Domain model (EF entities, sketch)

- `Artist` (Id, Mbid?, Name). `Mbid` is nullable; a unique index applies only when it is set.
- `Album` (Id, Mbid, Title, Year, ArtistId)
- `Track` (Id, RecordingMbid, Isrc?, Title, ArtistName, AlbumTitle?, ReleaseGroupMbid?, Year?, SourceUrl?, DurationMs). `ArtistName` is the display credit built from the artist links. Album, track number, and disc number are not persisted yet. YouTube-only tracks use a stable GUID derived from the video ID and store the video URL in `SourceUrl`.
- `LibraryImport` / `LibraryImportCandidate` (pending/identified/no-match/imported/rejected files, provider and source URL, release group and year, ranked candidates). Used by the existing-library import flow (§8.9).
- `DownloadJobCandidate` (Id, DownloadJobId, Rank, SourceUrl, Title, DurationMs?, Score, Reason?): up to 10 scored candidates per job, unique per (job, rank); removed with its job.
- `TrackArtist` (TrackId, ArtistId, Position): ordered artist credits with composite key (TrackId, Position). Position 0 is the primary artist and names the library folder; the artist tag lists all credits joined with `; `.
- `DownloadJob` (Id, TrackId, Status, Attempts, Error?, CreatedAt, StartedAt?, FinishedAt?, ProviderName, SourceUrl?, MatchScore?, OutputPath?)
- `LibraryFile` (Id, TrackId, RelativePath, Format, Bitrate?, SizeBytes, Sha256, AddedAt)
- `Setting` (Key, Value): quality, format, single-vs-album, library root, etc.
- `UserAccount` (single user): username, password hash
- `TrackInfo` (domain model, not EF entity): canonical track metadata using MusicBrainz IDs
  - `RecordingMbid` - MusicBrainz recording ID
  - `Artists` - `IReadOnlyList<ArtistCredit>` - all performing artists with MBIDs (fixes "composite artist C" bug)
  - `ArtistCredit` record: `{ Name, Mbid }` - individual artist attribution
  - `Isrc` - ISRC code, if available
  - `Title` - track title
  - `AlbumTitle` - album title, if applicable
  - `TrackNumber` - track number on the album
  - `DiscNumber` - disc number
  - `DurationMs` - duration in milliseconds
  - `IsSingle` - computed property
  - `GetPrimaryArtistName()` - returns first artist name
  - `GetPrimaryArtistMbid()` - returns first artist MBID

`DownloadJob.Status`: `Queued`, `Resolving`, `Searching`, `NeedsReview`, `Approved`, `Downloading`, `Tagging`, `ReadyForTagging`, `Done`, `Failed`, `Cancelled`.

Use **MusicBrainz IDs (MBIDs)** as canonical identifiers wherever possible.

## 8. Core behaviors

### 8.1 Search
- Query MusicBrainz server-side. Support `artist:name` style narrowing.
- Sort by relevance or popularity (ListenBrainz play counts).
- Cache responses (in-memory + DB, TTL of days; metadata rarely changes).

### 8.2 MusicBrainz client rules (MUST follow)
- Max **1 request/second** (global limiter, shared across all callers).
- Send a descriptive `User-Agent`: `AppName/Version ( contact-url-or-email )`. Requests without it may be blocked.
- Handle `503` and `Retry-After` with backoff.
- Implementation status: a singleton `MusicBrainzRequestGate` paces every MusicBrainz request in the process to one per second. `429` and `503` responses are retried up to three attempts, honouring `Retry-After` (capped at 60 s) and otherwise backing off 2 s and 4 s. Transport failures are retried the same way. When MusicBrainz still cannot answer, the client throws `MetadataUnavailableException`, and the API returns `503` with a Spanish message. Failures are no longer reported as empty results.
- Search uses the MusicBrainz search endpoints: `/ws/2/recording` for tracks, `/ws/2/release` for albums, and `/ws/2/artist` for artists. Previously tracks used `/ws/2/track`, which is not a search endpoint, and the resulting 404 was hidden as an empty result.
- The metadata cache uses stable request keys and a seven-day in-memory TTL. It is process-local; persistent caching remains future work.

### 8.3 Playlists
- Accept public Spotify playlist URLs or IDs only.
- Use public Spotify playlist pages or embed pages with no Spotify credentials. Do not require user OAuth or Client Credentials; page format changes may require parser maintenance.
- Map each Spotify track to a MusicBrainz recording (ISRC lookup first, then fuzzy title/artist/duration) before downloading.

### 8.4 Matching (the hard part)
Do **not** blindly take the first result.

1. Ask the provider for up to 10 candidates per track (owner decision). The yt-dlp provider searches `"<title> - <artist>"` with `ytsearch10:`; that form found the target video in testing, while `"<artist> <title>"` did not. Variants such as `"... audio"` are not used yet.
2. Score each candidate (0 to 1):
   - Duration within tolerance of the MusicBrainz length (strong signal)
   - Normalized title similarity (strip "feat.", "remastered", brackets, punctuation, case)
   - Artist/channel name similarity
   - Penalties for: `live`, `concert`, `remix`, `cover`, `karaoke`, `sped up`, `slowed`, `reverb`, `8d`, unless the target title itself contains them
   - Small bonus for "Topic" or official-audio style sources
3. Pick the best if score >= `AutoAcceptThreshold` (configurable, default ~0.80).
4. Between `ReviewThreshold` and `AutoAcceptThreshold`, set status `NeedsReview`. Below, mark `Failed` with reason `NoConfidentMatch`.

`IMatchScorer` MUST be pure and covered by table-driven unit tests.

Scorer gap status:

- Resolved: artist matching checks whole-word artist names in the candidate title. Channel/uploader names are not stored yet.
- Resolved: penalties compare against the target title and match whole words only.
- Resolved: the worker stores up to 10 ranked candidates per job (`DownloadJobCandidates`), and the job detail endpoint lists them. Approval can select any stored candidate by ID.
- Covered by scorer tests (`ScorerTests`, `ScorerSelectionTests`). An exact title match is capped at 0.3, so title and duration alone do not reach auto-accept.

### 8.5 Queue / worker
- `POST /api/downloads` accepts one track and returns immediately (`202 Accepted` + job id).
- The current hosted worker polls SQLite and processes the oldest `Queued` job first (FIFO, concurrency 1).
- Track and job creation are persisted together. At startup, jobs left in `Resolving` or `Searching` are re-queued.
- `GET /api/downloads` supports status filtering and bounded pagination (up to 100 items per page); queued results are ordered FIFO and include queue positions. `GET /api/downloads/{id}` returns an individual job status.
- `POST /api/downloads/{id}/approve` accepts only `NeedsReview`, records `Approved`, and preserves the selected source and score. An optional body `{ "candidateId": n }` selects a stored candidate of the same job as the source and score; a candidate from another job is rejected with 400. The worker then downloads the approved source, transcodes it to MP3, writes tags, stores it in the library, records a `LibraryFile`, and marks the job `Done`.
- `POST /api/downloads/{id}/cancel` cancels jobs only while `Queued` or `NeedsReview`; attempts to cancel active or terminal jobs return `409 Conflict`. Active provider work is not interrupted.
- Library import (§8.9): `POST /api/library/scan`, `GET /api/library/imports`, `GET /api/library/imports/{id}`, `POST /api/library/imports/identify`, `POST /api/library/imports/{id}/accept`, `POST /api/library/imports/{id}/reject`, `PUT /api/library/imports/{id}/path`, `POST /api/library/covers`. Implemented; see §8.9.
- `POST /api/downloads/{id}/retry` requeues `Failed` jobs, retaining the lifetime attempt count and clearing prior error/candidate details. Manual retries reset the automatic retry counter (`AutomaticRetries`) and clear `NextAttemptAt`. Manual retries have no configured limit.
- Automatic retries: a transient failure (yt-dlp rate limits and server errors, network failures, timeouts, or an unavailable metadata provider) returns the job to its queue with `NextAttemptAt` set. Retry n waits `Worker:RetryBaseDelaySeconds` x 2^(n-1) seconds (default 30, capped at 10 minutes), up to `Worker:MaxAutomaticRetries` retries (default 2). When the budget is spent, the job becomes `Failed` with `Failed after N attempts`. Permanent failures fail at once. Claims skip jobs whose `NextAttemptAt` is still in the future.
- Startup recovery: jobs in `Resolving` or `Searching` return to `Queued`; jobs in `Downloading` or `Tagging` return to `ReadyForTagging`, so the storage stage runs again. Partially written library files are not recovered automatically.
- `NeedsReview` jobs are parked while the worker continues processing other queued jobs.
- Registered providers: `deterministic` (tests only; it supplies no audio), `local` (audio files directly inside the source folder, default `tracks/`), and `yt-dlp` (YouTube search, selected video downloaded by its ID). Production requests default to `yt-dlp` when it is registered; test-only compositions without it fall back to `deterministic`. The request's `provider` field selects one. yt-dlp is intended for lawful, personally authorized archival use (§3).
- The accepted candidate is persisted with its score and source, then the job moves to `ReadyForTagging`. Before searching and again before storage, the worker checks `LibraryFiles` for the track and the resolved target path. If the recording is already stored, the job is marked `Done` with `AlreadyInLibrary` and the existing `OutputPath`; if only the physical target exists, the note is `AlreadyExists`. In both cases the provider is not called. Otherwise the worker downloads the source into a temporary work directory, transcodes it to MP3 with ffmpeg, fetches the release-group front cover from the Cover Art Archive, writes ID3 tags and the embedded cover with TagLibSharp, moves singles into `{Library__Root}/{PrimaryArtist}/{Title}.mp3` and album tracks into `{Library__Root}/{PrimaryArtist}/{Album}/{NN} - {Title}.mp3`, writes a `LibraryFile` row, and marks the job `Done`. Active jobs can be cancelled cooperatively; provider/transcoder cancellation cleans staging and marks the job `Cancelled`. The final `File.Exists` check remains as a last safety net. If the database save fails after the move, the moved file is deleted. Approved jobs follow the same path.
- `POST /api/downloads` rejects a recording that already has an active job (`Queued`, `Resolving`, `Searching`, `NeedsReview`, `Approved`, `Downloading`, `Tagging`, or `ReadyForTagging`) with `409 Conflict`. A recording that is already stored still returns `202` so the worker can record an `AlreadyInLibrary` job.
- Import acceptance checks the recording first, then computes the pre-embed SHA-256 and compares it with `LibraryFiles`. A matching hash returns `FileAlreadyInLibrary`, mapped to `409 Conflict`, and the file is left untouched, including no cover embedding. The final hash is recomputed after embedding for the stored row.
- The intended completed design remains a persisted, resumable queue with transient retries, idempotency, and progress over SSE.

### 8.6 Library layout
- Album tracks: `{LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}`.
- Singles (default for one-off track downloads, configurable): `{LibraryRoot}/{Artist}/{Title}.{ext}` with no album tag
- Whole-album download always uses album folders.
- For album downloads, use the tracklist most official editions agree on (MusicBrainz has many editions per release group; pick consensus, not the first result).
- Output formats: MP3, FLAC, Opus, AAC/M4A. Quality is a global setting, not chosen silently.

### 8.7 Artist "top N"
Use ListenBrainz `top-recordings-for-artist` (ranked by play count). Skip remaster, live and concert versions and near-duplicate titles. A ListenBrainz user token (optional setting) makes this endpoint more reliable.

### 8.9 Library import (implemented)
- Scans `Library:Root` for audio files laid out as `Artist/Title` or `Artist/Album/NN - Title`. Other layouts are reported as unrecognized. Tags are not read yet.
- Each file becomes a `LibraryImport` (`Pending`). Identification searches MusicBrainz for `Title - Artist` and stores up to 10 `LibraryImportCandidate` rows, ranked by the scorer. The title is compared without the artist, and the artist is compared separately (`ArtistMatched`).
- When no MusicBrainz candidate matches the artist, the service searches YouTube through yt-dlp (`--flat-playlist --dump-json --skip-download`) and adds up to `Library:YouTubeMaxCandidates` videos (default 5). Every candidate records `Provider` (`musicbrainz` or `youtube`) and `SourceUrl`. The parsed video artist and title are scored separately. The fallback is controlled by `Library:YouTubeFallback` (default `true`).
- A file is `Identified` when a candidate is above the review threshold and matches the artist; otherwise `NoMatch`. Candidates stay available for manual choice. A YouTube failure is recorded on the import as `YouTube: ...`; MusicBrainz candidates are kept and the rest of the run continues.
- Accepting a candidate creates or reuses the track with its artist credits, including `ReleaseGroupMbid` and `Year` when the candidate came from MusicBrainz, and writes a `LibraryFile`. The file is never downloaded or moved. For a MusicBrainz candidate, the release-group front cover is fetched from the Cover Art Archive and embedded into the existing file, leaving its text tags untouched; cover failures do not block the import. This is controlled by `Library:EmbedCoverOnImport` (default `true`). A recording already in the library is refused.
- A YouTube-only track stores a stable GUID derived from the video ID as `RecordingMbid` and the video URL in `Tracks.SourceUrl`. On accept, the artist and title come from the parsed video title; if the video title has no `Artist - Title` separator, the folder values are used.
- `POST /api/library/covers` backfills covers for files imported before cover embedding existed. It targets `Imported` entries whose track has a `ReleaseGroupMbid`, embeds the front cover without touching text tags, and refreshes `LibraryFile.Sha256`/`SizeBytes`. An optional `{"importIds": [..]}` limits the batch. Missing covers are skipped; missing files and write errors are counted as failed and do not abort the run.
- Stored paths can be changed manually. The new path must stay inside the library root and point to an existing file.

### 8.8 Analytics (start small)
Endpoints/queries over the local DB: top artists by tracks owned, downloads per day, success/failure rate by provider, average match score, album completeness (owned tracks vs MusicBrainz tracklist), orphan or untagged files.

## 9. Improvements over the reference project

| Reference (FreeListen) | MusicSync direction and current status |
|---|---|
| Takes first `ytsearch1` result | Searches up to 10 yt-dlp candidates, scores them, stores the ranked list, and lets the user approve any of them. |
| In-memory queue and activity log | SQLite-backed FIFO jobs, list/detail/approve/cancel/retry endpoints, and automatic retry/backoff are implemented; SSE is not. |
| MusicBrainz called from browser | Server-side metadata clients exist; verify global rate limiting, caching, and resilience before treating them as complete. |
| UI polls for activity | No UI or SSE yet; both remain planned. |
| Spotify playlist support | Playlist parsing/API foundation exists; bulk playlist-to-job import is not implemented. |
| Tagging pipeline | ffmpeg transcodes to MP3, then TagLibSharp writes title, artists, album, year, track/disc numbers, ISRC, MBIDs, and an optional embedded front cover fetched from the Cover Art Archive. |
| Library and duplicate detection | Completed files and accepted imports are stored with `LibraryFile` rows. Duplicate-safe admission is implemented: active-job MBID conflicts return `409`, stored recordings are marked `AlreadyInLibrary`, physical target conflicts are marked `AlreadyExists`, and import acceptance rejects the same recording or the same SHA-256. Cover art can be backfilled, and general listing/detail/deletion are implemented. |
| Multi-artist metadata | Artist credits are stored in order in `TrackArtists`. The first artist names the folder and every artist is written to the artist tag. Album layout and per-artist bulk preview/enqueue are implemented. |

These are roadmap comparisons, not claims that the target behavior is already shipped. The near-term goal is a complete, safe single-track vertical slice before bulk workflows or UI work.

## 10. HTTP API (sketch)

All routes under `/api`, JSON, HTTPS, authenticated except `/api/health` and first-run `/api/auth/setup`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/auth/status` | Setup/sign-in state |
| POST | `/auth/setup` | Create the single account (first run only) |
| POST | `/auth/login`, `/auth/logout`, `/auth/password` | Session management |
| GET | `/health` | Liveness |
| POST | `/library/scan`, `/library/imports/identify` | Scan and identify existing library files (§8.9) |
| GET | `/library`, `/library/{id}` | List and inspect stored library files |
| DELETE | `/library/{id}` | Delete a contained library file and prune empty directories |
| GET | `/library/imports`, `/library/imports/{id}` | List imports and inspect ranked candidates (§8.9) |
| POST | `/library/imports/{id}/accept`, `/library/imports/{id}/reject` | Accept or discard an import match (§8.9) |
| PUT | `/library/imports/{id}/path` | Correct an imported file's stored path (§8.9) |
| POST | `/library/covers` | Backfill front covers for imported files (§8.9) |
| GET | `/search?type=track\|album\|artist&q=&sort=` | MusicBrainz search |
| GET | `/artists/{mbid}/top?count=` | Top recordings (ListenBrainz) |
| POST | `/playlists/preview` | Resolve a public Spotify playlist and persist MusicBrainz candidates |
| GET | `/playlists/{id}` | Read a persisted playlist preview |
| POST | `/playlists/{id}/enqueue` | Enqueue selected confirmed playlist items |
| POST | `/youtube/preview` | Read a canonical YouTube watch URL and return parsed metadata plus MusicBrainz candidates |
| POST | `/youtube/enqueue` | Queue a selected YouTube candidate through the yt-dlp provider |
| POST | `/downloads` | Enqueue a single track |
| GET | `/downloads` | List jobs (filter by status) |
| GET | `/downloads/{id}` | Get a job and its current status |
| POST | `/downloads/{id}/approve` | Approve a `NeedsReview` candidate |
| POST | `/downloads/{id}/cancel` | Cancel a queued or review-pending job |
| POST | `/downloads/{id}/retry` | Requeue a failed job |
| GET | `/events?jobId=&after=` | Authenticated persisted SSE stream of job progress |
| GET | `/library` | List saved files |
| DELETE | `/library/{id}` | Delete a file and prune empty folders |
| GET/PUT | `/settings` | Settings status and write-only encrypted ListenBrainz token |
| POST | `/settings/secrets/generate` | Generate and encrypt an application secret without returning its value |
| GET | `/analytics/summary?topArtistLimit=10` | Database-backed library, job, completeness, and top-artist summary |

Implementation status: under `/api/downloads`, create (with optional `provider`), list, detail, approve, cancel, and retry are implemented. Responses use an `ApiResponse<T>` envelope with `status`, `msg`, and `data`. `GET /` returns the static UI. `/api/health` and the auth endpoints (`status`, `setup`, `login`, `logout`, `logout-all`, `password`) are implemented. Search, playlist, album, artist, and YouTube-link preview/enqueue endpoints exist. Existing-library scan/import/accept/reject/path-correction, cover backfill, general library list/detail/delete, persisted authenticated SSE events, encrypted settings status/update, application-secret generation, and database-backed analytics are implemented. Final deployment verification remains.

## 11. Security requirements (MUST)

- **Path safety:** the library root is fixed by config. Any user-supplied path component MUST be sanitized, then the final path MUST be `Path.GetFullPath`-resolved and verified to start with the library root. Applies to download, delete and read-back. Reject `..`, absolute paths, and reserved names.
- **No shell injection:** never build command lines by string concatenation. Use `ProcessStartInfo.ArgumentList`. Never pass user input as a flag (use `--` before positional args; validate URLs/IDs).
- **Secrets:** `.env.example` documents deployment variables and `.env` is ignored by Git, but dotenv values are plaintext and are not an encrypted secret store. Environment variables take precedence. The Settings API/UI stores ListenBrainz and generated application secrets encrypted with ASP.NET Data Protection; GET responses expose only configured flags and never plaintext.
- **Auth:** passwords hashed with Argon2id or ASP.NET `PasswordHasher`. Cookie auth with `HttpOnly`, `Secure`, `SameSite=Strict`. Until setup is complete, all routes except health and setup return 403. Implemented: ASP.NET `PasswordHasher` (PBKDF2, salted); session cookie `HttpOnly` and `SameSite=Strict`, with `Secure` controlled by `Auth__SecureCookies` (default off so plain HTTP works); cookie-protection keys persisted under `Config__Dir`; first-run 403 gate; five-per-five-minutes login/setup rate limit per client IP; security-stamp session revocation through `POST /api/auth/logout-all`; encrypted write-only settings storage.
- **Inbound hardening:** authentication rate limiting and a 1 MiB request body limit are implemented; strict CORS remains disabled by default. HTTPS redirection, HSTS, and broader request-specific size limits remain future work. The owner accepted plain HTTP for the current private deployment; HTTPS remains selectable through `Auth__SecureCookies` and the hosting configuration.
- **Outbound hardening:** allowlist hosts for cover-art fetching (e.g. `coverartarchive.org`, `archive.org`). Reject others (SSRF protection).
- **Docker:** run as non-root, mount only the library and config volumes.

## 12. Configuration

Bind via `IOptions<T>` from `appsettings.json` and environment variables:

```
Library__Root=/music
Audio__Format=mp3            # mp3|flac|opus|m4a
Audio__Quality=0             # encoder quality, meaning depends on format
Matching__AutoAcceptThreshold=0.80
Matching__ReviewThreshold=0.50
Matching__DurationToleranceSeconds=5
Worker__Concurrency=1
MusicBrainz__UserAgent=MusicSync/0.1 (https://github.com/ismabellmann/musicsearcher)  # default; use an email or URL of your own if preferred
ListenBrainz__Token=         # optional; use a private .env or deployment secret, never commit it
Playlist__MaxTracks=100
Playlist__MaxCandidates=10
```

Implementation status of this list:

- Read by the code: `ASPNETCORE_URLS` (default `http://localhost:5000`), `Config__Dir`, `Auth__SecureCookies`, `Auth__SessionHours`, `Data__DatabasePath`, `Library__Root` (default: `tracks/` at the repository root), `Library__StagingDirectory` (default: `data/staging` under the API content root), `Library__YouTubeFallback` (default `true`), `Library__YouTubeMaxCandidates` (default `5`), `Library__EmbedCoverOnImport` (default `true`), `Acquisition__LocalSourceDirectory` (default: `tracks/`), `Audio__Quality`, `CoverArt__Enabled` (default `true`; applies to downloads, imported files and backfill), `CoverArt__MaxBytes` (default `5000000`), `CoverArt__TimeoutSeconds` (default `15`), `Tools__Ffmpeg`, `Tools__Ffprobe`, `Tools__YtDlp`, `Worker__MaxAutomaticRetries`, `Worker__RetryBaseDelaySeconds`, `Matching__AutoAcceptThreshold`, `Matching__ReviewThreshold`, `Matching__DurationToleranceSeconds`, `MusicBrainz__UserAgent` (shared with ListenBrainz requests), `ListenBrainz__Token`, `Playlist__MaxTracks`, and `Playlist__MaxCandidates`.
- The repository's `.env.example` is the Docker/production template. Copy it to a Git-ignored `.env`, set mode `600`, and never commit or log it. Dotenv values remain plaintext deployment overrides; use the authenticated Settings API/UI when encrypted storage is required.
- Development defaults: when `ASPNETCORE_ENVIRONMENT=Development` (set by the `MusicSync.Api` launch profile), `Config__Dir` and `Data__DatabasePath` default to a git-ignored `config/` folder at the repository root. Production must set both explicitly to persistent paths, for example `/config` and `/config/musicsync.db` in the container.
- Validated at startup (the app stops with a clear error if they are invalid): `MusicBrainz__UserAgent` must look like `Name/Version (contact)`; `Matching__*` thresholds must be between 0 and 1, with review not above auto-accept; `Audio__Format` must be `mp3`; `Worker__Concurrency` must be `1`.
- Not read yet: `Auth__Disable` was removed from the sample; there is no authentication bypass.

## 13. Coding conventions

- C# 12, file-scoped namespaces, `record` for DTOs and value objects, nullable enabled, warnings as errors in CI.
- `async` all the way, always accept and forward `CancellationToken`.
- No `static` HTTP clients. Use `IHttpClientFactory`.
- Keep `Core` free of I/O. Put pure logic (normalization, scoring, path building) there so it's trivially testable.
- Small, focused classes. No god services.
- Every external API call goes through a typed client with timeouts and retry policy.
- Log with structured templates (`"Job {JobId} failed: {Reason}"`), never string interpolation, never secrets.
- Prefer clear error results (`Result<T>` or domain exceptions) over returning `null` for failures.

## 14. Milestones

Progress below reflects the current repository implementation. A passing test/build validates only the exercised code paths; it does not imply the remaining milestone features are complete.

1. **M1 Skeleton:** solution layout, project references, API composition, EF entities, SQLite registration, metadata-client DI, and EF Core migrations are present. The API applies pending migrations at startup and safely baselines a matching legacy `EnsureCreated` SQLite schema. Startup validates configuration (MusicBrainz identity, matching thresholds, audio format, worker concurrency, retry settings). ✅ Implemented - first-run setup, authenticated cookie sessions, health endpoint, and configuration validation are complete. HTTPS/HSTS enforcement remains optional for the current plain-HTTP private deployment; inbound hardening is tracked in §11.
2. **M2 Metadata:** MusicBrainz, ListenBrainz, and Spotify clients, API endpoints, and parser tests are present. 🚧 In progress - MusicBrainz uses a reusable seven-day in-memory cache and a shared one-request-per-second gate; MusicBrainz and ListenBrainz retry transient failures with `Retry-After` support, and provider failures surface as 503 instead of empty results. Front-cover retrieval from the Cover Art Archive is implemented for downloads, accepted imports, and backfill, with redirect allowlisting and a size cap. Persistent metadata caching, broader provider error tests, and remaining resilience work are future improvements.
3. **M3 Pipeline:** track/job persistence, SQLite-backed FIFO worker polling, startup recovery, three acquisition providers (`deterministic`, `local`, `yt-dlp`), best-of-10 scoring, job list/detail/approve/cancel/retry endpoints, queue position, `NeedsReview` parking, approved jobs continuing through download, ffmpeg MP3 transcode, TagLibSharp tags with an optional embedded Cover Art Archive front cover, singles and album-safe library paths, cooperative active-job cancellation, a library-root containment check, duplicate-safe admission by recording MBID, SHA-256 and target path, `LibraryFile` persistence, normalized artist credits, automatic retries with backoff for transient failures, recovery of interrupted downloads on restart, and front-cover embedding/backfill for imported files. 🚧 In progress - remaining operational hardening.
4. **M4 Matching:** core scorer, artist matching, target-aware penalties, exact-title cap, best-of-10 selection, stored candidate lists, user selection among candidates at approval, `NeedsReview`, and consumption of approved jobs are implemented. ✅ Implemented for the single-track pipeline; channel-name matching remains future work.
5. **M5 Playlists and top-N:** Public Spotify playlist parsing, persisted preview/matching, selected enqueue, MusicBrainz album preview/enqueue, and ListenBrainz artist top-recording preview/enqueue are present. 🚧 In progress - live validation against Spotify page variations remains.
6. **M6 Realtime and library:** ✅ Implemented - `LibraryFile` rows record downloaded and imported files, duplicate admission by recording MBID, SHA-256, and target path is implemented, general listing/detail/delete endpoints are available, and persisted authenticated SSE job events support replay with `Last-Event-ID`. UI integration and event retention maintenance remain.
7. **M7 Analytics and packaging:** 🚧 In progress - Dockerfile, TrueNAS compose reference, GHCR workflow, non-root UID/GID `568:568`, amd64 image, and pinned yt-dlp `2026.10.26` are implemented. First GHCR publication and NAS deployment remain to be verified.
8. **M8 UI:** 🚧 In progress - dependency-free static frontend with Search, Activity, Library, Playlists, Analytics, Settings, authentication, responsive layout, and system/light/dark theme toggle. The next UI slice is the album library view with ratings and genre metadata.

### Recommended implementation order

1. Complete one single-track pipeline end to end using only an acquisition source the user is authorized to access: scorer correctness, approved-candidate behavior, acquisition, MP3 conversion, tagging, safe library path, duplicate detection, and persisted `LibraryFile`. This vertical slice is implemented, including duplicate-safe admission.
2. Verify job lifecycle and failure recovery for that vertical slice with integration tests; do not mark a job `Done` until the file and database record are successfully committed.
3. Add bulk enqueue/import workflows beyond public Spotify playlists, starting with album and artist top-N flows.
4. Add SSE/activity and library list/delete endpoints, followed by the minimal UI that exposes Search, Activity, Library, Analytics, and Settings.
5. Prioritize security, configuration, and deployment packaging needed for the chosen target; defer secondary features until the core workflow is dependable.

### Completed slice: duplicate-safe library admission (M3/M6)

Implemented and covered by tests. Final behavior:
- `POST /api/downloads` rejects an active job for the same recording with `409 Conflict` and creates no second job. A recording that is already stored still returns `202` so the worker can record an `AlreadyInLibrary` job.
- The worker checks `LibraryFiles` for the track and the resolved target path before searching and again before storage. It marks the job `Done` with `AlreadyInLibrary` (database record exists) or `AlreadyExists` (physical target exists), sets `OutputPath`, and does not call the provider. The final `File.Exists` check remains as a last safety net.
- Import acceptance checks the recording first, then the pre-embed SHA-256. A matching hash returns `FileAlreadyInLibrary`, mapped to `409 Conflict`, without modifying the file or embedding a cover. The final hash is recomputed after a successful embedding for the stored row.
- No schema change was needed. Indexes on `LibraryFiles.TrackId`/`Sha256` remain optional performance work.

Tests cover active-job conflicts, existing stored recordings, existing target paths, import hash duplicates, and staging exclusion (Core 83, Infrastructure 108).

### Completed slice: playlist, album, and artist bulk preview/enqueue (M5)

Implemented without Spotify credentials:
- `POST /api/playlists/preview` resolves a public Spotify embed page, persists the playlist and its ordered tracks, and stores up to `Playlist__MaxCandidates` MusicBrainz candidates per item.
- `GET /api/playlists/{id}` returns the persisted preview and candidate statuses (`Matched`, `NeedsReview`, `NoMatch`, or `Enqueued`).
- `POST /api/playlists/{id}/enqueue` enqueues selected confirmed candidates, reusing normal track/job persistence and active-job duplicate protection. Repeated enqueue requests are idempotent.
- `POST /api/playlists/album/{mbid}/preview` previews all recordings returned for a MusicBrainz release group, and `POST /api/playlists/artist/{mbid}/preview?artistName=...&count=...` previews ListenBrainz top recordings mapped back to MusicBrainz.
- Public-page parsing currently uses Spotify's `__NEXT_DATA__` embed payload. Spotify page format changes may require parser updates.

The next follow-up is live validation against several public page variants and adding explicit item-level candidate selection tests through the API.

### Completed slice: library management and realtime activity (M6)

- `GET /api/library` and `GET /api/library/{id}` provide paginated metadata-safe listing/detail views with artist credits and imported/downloaded origin.
- `DELETE /api/library/{id}` resolves paths through `LibraryPathGuard`, deletes only contained files, resets matching imported records to `Pending`, and prunes empty child directories.
- `GET /api/events` streams persisted `DownloadJobEvent` records, supports `jobId`, `after`, and `Last-Event-ID`, sends keep-alives, and broadcasts worker/controller lifecycle changes.
- No filesystem absolute paths are exposed. Missing files are treated as idempotent deletion cleanup.

Deployment remains intentionally deferred until all application milestones, UI, and hardening are complete.

### Completed slice: security and provider reliability baseline

- MusicBrainz responses now use stable seven-day in-memory cache keys and preserve the global one-request-per-second gate.
- ListenBrainz retries transient HTTP failures with bounded backoff and `Retry-After`; exhausted provider failures use `MetadataUnavailableException` and the API's 503 response path.
- Authentication setup/login are rate-limited to five requests per five-minute window per client IP. `POST /api/auth/logout-all` rotates the account security stamp and invalidates existing cookies.
- API request bodies are limited to 1 MiB, baseline security headers are emitted, and library path resolution rejects existing symlink/reparse-point traversal.
- Remaining security work: endpoint-specific request limits, HTTPS/HSTS validation, and broader inbound hardening.

The application staging directory defaults to `/music/.musicsync-staging` in the image so the
final `File.Move` stays on the same filesystem as the library. Library scans explicitly skip
that directory.

Remaining after this slice: targeted security follow-ups, UI API polish, and final deployment verification.

### Next planned slice: album library, ratings, and genre metadata (M6/M8)

Adapt the existing lightweight UI toward the `mi_biblioteca_v3.html` visual language while keeping
the no-framework/no-build/resource-conscious architecture:

- Add an album-oriented library API that groups `LibraryFile`/`Track` rows by release-group MBID,
  with a stable normalized artist+album fallback key when no MBID exists.
- Expose album title, primary/all artists, year, release-group MBID, track count, owned count,
  completeness, total size, cover availability, rating, notes, and genre.
- Add an SQLite `Ratings` entity and EF migration with `TargetType`, `TargetKey`, `RatingValue`
  (`0` to `5`), optional notes, and created/updated timestamps. Enforce one rating per target with
  a unique `(TargetType, TargetKey)` index. The model supports `album` and `track`, while the first
  UI focuses on album ratings.
- Use target keys `album:{release-group-mbid}` and `track:{recording-mbid}`. Albums without a
  release-group MBID use a deterministic hash of normalized primary artist and album title.
- Add authenticated rating endpoints: `GET`, `PUT`, and `DELETE /api/ratings/{targetType}/{targetKey}`.
  Ratings of `0` or DELETE clear the rating; notes are bounded and never stored in localStorage.
- Add `GET /api/library/albums` and `/api/library/albums/{key}` with pagination, search, and sorting
  by title, artist, year, rating, and completeness.
- Extend `TrackInfo`, `Track`, search results, library details, album views, and MusicBrainz parsing
  with optional normalized genre data. Preserve multiple genres as `; `-joined text when available.
  If MusicBrainz has no genre, the API/UI displays `—` and does not invent a value.
- Replace the current file-first library view with the adapted album grid/list, tag/filter toolbar,
  CSS star display, album detail modal, rating editor, notes, track list, and completeness progress.
- Extend analytics with rated/unrated album counts, average album rating, and top-rated albums.

Constraints:
- No external fonts, icon libraries, frontend frameworks, or build step.
- Use existing CSS variables and theme toggle; add the warm paper/orange and vinyl-inspired visual
  treatment without copying the prototype's localStorage data model or external iTunes requests.
- Existing tracks without genre metadata remain valid and display `—`; metadata backfill can be a
  later operation using recording MBIDs.

Tests required: rating CRUD/validation, unique target updates, stable fallback keys, album grouping,
completeness, genre parsing/fallback, API authorization, and UI/API smoke coverage.

## 15. Open questions (ask the owner before assuming)

- The host config folder `/mnt/tank/Servidores/musicsync` still has to be created on the NAS before the container can persist its database and keys.
- The first GHCR publication must be made public, or registry credentials must be configured in TrueNAS.
- NAS deployment and upgrade persistence still require live verification.

Owner decisions already recorded:

- Deployment: Docker on TrueNAS as a custom app. Container listens on port 28080 (not 8080) to avoid clashes with other projects on the host.
- Music library host path: `/mnt/tank/ftp/musica`, mounted at `/music`. It already contains tracks that should be importable without download.
- Config/database host path: `/mnt/tank/Servidores/musicsync`, mounted at `/config`. The folder must be created on the NAS and writable by UID/GID `568:568`.
- Spotify playlists use the public-page method, without credentials.
- Single-user service.
- MP3 is the intended default output format.
- Use yt-dlp as the intended acquisition provider, subject to lawful use and service terms.
- `NeedsReview` jobs park without blocking later queued jobs.
- Queue ordering is FIFO; first queue-management scope is observing/managing jobs, not batch enqueue.
- Use EF Core migrations for future schema changes; do not use `EnsureCreated` for application startup or database upgrades.
- Publish the Docker image to GHCR through GitHub Actions, target `linux/amd64`, pin yt-dlp `2026.10.26`, and run the container as UID/GID `568:568`.

## 16. Instructions to the AI assistant

- Work milestone by milestone. Don't scaffold everything at once.
- Before writing code that touches paths, processes or external APIs, re-read sections 8.2, 8.4 and 11.
- Write or update unit tests with each change, especially for `IMatchScorer`, filename sanitization and path resolution.
- If a requirement here conflicts with an API's actual current behavior (e.g. Spotify endpoint availability), say so and propose an alternative instead of guessing.
- Keep the acquisition provider isolated, as described in section 3.
