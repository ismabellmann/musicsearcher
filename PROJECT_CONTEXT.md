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

- The acquisition step MUST sit behind an interface (`IAcquisitionProvider`). Nothing outside that provider may reference YouTube, SoundCloud or yt-dlp.
- Keep acquisition providers replaceable; support only sources the user is authorized to acquire. The owner selected yt-dlp as an intended provider, but implementation and use must remain subject to law and service terms. Other possible providers include local import and sources the user owns or is authorized to download.
- The README MUST include a short disclaimer that users are responsible for complying with applicable laws and ToS.

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
                         │ (Channel+DB) │        └────────────┘
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

Dependency rule: `Core` depends on nothing. `Infrastructure` and `Worker` depend on `Core`. `Api` composes everything.

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

public interface IAcquisitionProvider         // yt-dlp today, others later
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct);
    Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, AudioFormatOptions format, string workDir, CancellationToken ct);
}

public interface IMatchScorer                 // pure function, heavily unit-tested
{
    MatchResult Score(TrackInfo target, SourceCandidate candidate);
}

public interface ITagWriter
{
    Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct);
}

public interface ILibraryPathResolver         // single place that builds and validates paths
{
    string Resolve(TrackInfo track, bool asSingle, string extension);
}
```

## 7. Domain model (EF entities, sketch)

- `Artist` (Id, Mbid, Name)
- `Album` (Id, Mbid, Title, Year, ArtistId)
- `Track` (Id, RecordingMbid, Isrc?, Title, ArtistId, AlbumId?, TrackNumber?, DiscNumber?, DurationMs)
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

### 8.3 Playlists
- Accept public Spotify playlist URLs or IDs only.
- Prefer the official Web API with the **Client Credentials** flow (read-only, no user data). Verify which endpoints are currently available to new apps, because Spotify has restricted some.
- Map each Spotify track to a MusicBrainz recording (ISRC lookup first, then fuzzy title/artist/duration) before downloading.

### 8.4 Matching (the hard part)
Do **not** blindly take the first result.

1. Ask the provider for up to ~5 candidates per track (query like `"<artist> <title>"`, and optionally variants such as `"... audio"` or `"... lyrics"`).
2. Score each candidate (0 to 1):
   - Duration within tolerance of the MusicBrainz length (strong signal)
   - Normalized title similarity (strip "feat.", "remastered", brackets, punctuation, case)
   - Artist/channel name similarity
   - Penalties for: `live`, `concert`, `remix`, `cover`, `karaoke`, `sped up`, `slowed`, `reverb`, `8d`, unless the target title itself contains them
   - Small bonus for "Topic" or official-audio style sources
3. Pick the best if score >= `AutoAcceptThreshold` (configurable, default ~0.80).
4. Between `ReviewThreshold` and `AutoAcceptThreshold`, set status `NeedsReview`. Below, mark `Failed` with reason `NoConfidentMatch`.

`IMatchScorer` MUST be pure and covered by table-driven unit tests.

Known implementation gaps to address before relying on scores:

- The current artist-score calculation returns zero; candidate/channel artist matching is not implemented.
- Candidate penalty evaluation does not receive the target title, so it cannot exempt features that are present in the requested track.
- The current worker stores only the single best candidate. It does not expose a candidate list or allow the user to select among alternatives.
- Verify and correct these scorer behaviors with focused tests before connecting automatic acceptance to a real acquisition pipeline.

### 8.5 Queue / worker
- `POST /api/downloads` accepts one track and returns immediately (`202 Accepted` + job id).
- The current hosted worker polls SQLite and processes the oldest `Queued` job first (FIFO, concurrency 1).
- Track and job creation are persisted together. At startup, jobs left in `Resolving` or `Searching` are re-queued.
- `GET /api/downloads` supports status filtering and bounded pagination (up to 100 items per page); queued results are ordered FIFO and include queue positions. `GET /api/downloads/{id}` returns an individual job status.
- `POST /api/downloads/{id}/approve` accepts only `NeedsReview`, records `Approved`, and preserves the selected source and score. This records the user's decision only; the worker does not yet consume approved jobs or start acquisition.
- `POST /api/downloads/{id}/cancel` cancels jobs only while `Queued` or `NeedsReview`; attempts to cancel active or terminal jobs return `409 Conflict`. Active provider work is not interrupted.
- `POST /api/downloads/{id}/retry` requeues `Failed` jobs, retaining the lifetime attempt count and clearing prior error/candidate details. Manual retries have no configured limit; automatic transient retry/backoff is not implemented.
- `Downloading` recovery is not implemented.
- `NeedsReview` jobs are parked while the worker continues processing other queued jobs.
- The deterministic provider is currently registered by the API. It supports repeatable pipeline tests but does not acquire audio. The `YtDlpAcquisitionProvider` is not yet a production implementation or the registered default.
- The accepted candidate is persisted with its score and source, then the job stops at `ReadyForTagging`. Actual download, transcode, tagging, library persistence, and SSE progress remain future work.
- Target behavior (not implemented): if the target file already exists (same MBID or same sanitized `Artist/Album/Title`), skip and mark `Done` with note `AlreadyExists`.
- The intended completed design remains a persisted, resumable queue with transient retries, idempotency, and progress over SSE.

### 8.6 Library layout
- Album tracks: `{LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}`
- Singles (default for one-off track downloads, configurable): `{LibraryRoot}/{Artist}/{Title}.{ext}` with no album tag
- Whole-album download always uses album folders.
- For album downloads, use the tracklist most official editions agree on (MusicBrainz has many editions per release group; pick consensus, not the first result).
- Output formats: MP3, FLAC, Opus, AAC/M4A. Quality is a global setting, not chosen silently.

### 8.7 Artist "top N"
Use ListenBrainz `top-recordings-for-artist` (ranked by play count). Skip remaster, live and concert versions and near-duplicate titles. A ListenBrainz user token (optional setting) makes this endpoint more reliable.

### 8.8 Analytics (start small)
Endpoints/queries over the local DB: top artists by tracks owned, downloads per day, success/failure rate by provider, average match score, album completeness (owned tracks vs MusicBrainz tracklist), orphan or untagged files.

## 9. Improvements over the reference project

| Reference (FreeListen) | MusicSync direction and current status |
|---|---|
| Takes first `ytsearch1` result | Designed for candidate scoring and review; scorer gaps remain and the registered provider is deterministic, not a real source search. |
| In-memory queue and activity log | SQLite-backed FIFO jobs and basic list/detail/cancel/retry endpoints are implemented; SSE and automatic retry/backoff are not. |
| MusicBrainz called from browser | Server-side metadata clients exist; verify global rate limiting, caching, and resilience before treating them as complete. |
| UI polls for activity | No UI or SSE yet; both remain planned. |
| Spotify playlist support | Playlist parsing/API foundation exists; bulk playlist-to-job import is not implemented. |
| ffmpeg tagging pipeline | ffmpeg and TagLibSharp pipeline is not implemented; MP3 is the selected intended default. |
| Library and duplicate detection | Path resolver and entity foundations exist; completed file persistence, listing/deletion, and deduplication are not implemented. |
| Multi-artist metadata | `TrackInfo` supports artist credits, but persisted track mapping and library indexing do not yet provide end-to-end multi-artist attribution. |

These are roadmap comparisons, not claims that the target behavior is already shipped. The near-term goal is a complete, safe single-track vertical slice before bulk workflows or UI work.

## 10. HTTP API (sketch)

All routes under `/api`, JSON, HTTPS, authenticated except `/api/health` and first-run `/api/auth/setup`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/auth/status` | Setup/sign-in state |
| POST | `/auth/setup` | Create the single account (first run only) |
| POST | `/auth/login`, `/auth/logout`, `/auth/password` | Session management |
| GET | `/health` | Liveness |
| GET | `/search?type=track\|album\|artist&q=&sort=` | MusicBrainz search |
| GET | `/artists/{mbid}/top?count=` | Top recordings (ListenBrainz) |
| GET | `/playlists?url=` | Resolve a public Spotify playlist |
| POST | `/downloads` | Enqueue track(s), album or playlist |
| GET | `/downloads` | List jobs (filter by status) |
| GET | `/downloads/{id}` | Get a job and its current status |
| POST | `/downloads/{id}/approve` | Approve a `NeedsReview` candidate |
| POST | `/downloads/{id}/cancel` | Cancel a queued or review-pending job |
| POST | `/downloads/{id}/retry` | Requeue a failed job |
| GET | `/events` | SSE stream of job progress |
| GET | `/library` | List saved files |
| DELETE | `/library/{id}` | Delete a file and prune empty folders |
| GET | `/analytics/summary` | Aggregates |
| GET/POST | `/settings` | Settings (secrets write-only, never returned) |

## 11. Security requirements (MUST)

- **Path safety:** the library root is fixed by config. Any user-supplied path component MUST be sanitized, then the final path MUST be `Path.GetFullPath`-resolved and verified to start with the library root. Applies to download, delete and read-back. Reject `..`, absolute paths, and reserved names.
- **No shell injection:** never build command lines by string concatenation. Use `ProcessStartInfo.ArgumentList`. Never pass user input as a flag (use `--` before positional args; validate URLs/IDs).
- **Secrets:** (Spotify client secret, ListenBrainz token) stored encrypted via ASP.NET Data Protection, write-only through the API, never logged, never returned.
- **Auth:** passwords hashed with Argon2id or ASP.NET `PasswordHasher`. Cookie auth with `HttpOnly`, `Secure`, `SameSite=Strict`. Until setup is complete, all routes except health and setup return 403.
- **Inbound hardening:** rate limiting, request size limits, strict CORS (default: none), HTTPS redirection and HSTS.
- **Outbound hardening:** allowlist hosts for cover-art fetching (e.g. `coverartarchive.org`, `archive.org`). Reject others (SSRF protection).
- **Docker:** run as non-root, mount only the library and config volumes.

## 12. Configuration

Bind via `IOptions<T>` from `appsettings.json` and environment variables:

```
Library__Root=/music
Config__Dir=/config
Audio__Format=mp3            # mp3|flac|opus|m4a
Audio__Quality=0             # encoder quality, meaning depends on format
Matching__AutoAcceptThreshold=0.80
Matching__ReviewThreshold=0.50
Matching__DurationToleranceSeconds=5
Worker__Concurrency=1
MusicBrainz__UserAgent=MusicSync/0.1 ( you@example.com )
ListenBrainz__Token=         # optional
Spotify__ClientId=           # optional
Spotify__ClientSecret=       # optional
Auth__Disable=false          # only behind an authenticating reverse proxy
```

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

1. **M1 Skeleton:** solution layout, project references, API composition, EF entities, SQLite registration, metadata-client DI, and EF Core migrations are present. The API applies pending migrations at startup and safely baselines a matching legacy `EnsureCreated` SQLite schema. 🚧 In progress - authentication, health endpoint, and HTTPS enforcement remain.
2. **M2 Metadata:** MusicBrainz, ListenBrainz, and Spotify clients, API endpoints, and parser tests are present. 🚧 In progress - cover-art retrieval, cache correctness, verified rate limiting/resilience, error/response consistency across all endpoints, and broader endpoint tests remain.
3. **M3 Pipeline:** track/job persistence, SQLite-backed FIFO worker polling, startup recovery for `Resolving`/`Searching`, deterministic acquisition, best-candidate scoring, job list/detail APIs, queue position, queued/review cancellation, manual retry for failed jobs, and persistence through `ReadyForTagging` are implemented. 🚧 In progress - complete the single-track vertical slice: fix scorer gaps; decide how approved candidates enter the pipeline; implement an authorized real acquisition path, MP3 conversion, TagLibSharp tagging, safe final-path resolution/tests, LibraryFile persistence, deduplication, automatic retries, active-work cancellation, and SSE.
4. **M4 Matching:** core scorer, unit tests, best-candidate selection, `NeedsReview`, and approval recording are implemented. 🚧 In progress - artist matching/target-aware penalties, candidate comparison and selection, and consumption of approved candidates by the acquisition pipeline remain.
5. **M5 Playlists and top-N:** Spotify playlist and ListenBrainz top-recording parsing foundations and API endpoints are present. 🚧 In progress - end-to-end playlist track mapping and enqueue/import workflow remain.
6. **M6 Realtime and library:** ⏳ Planned - SSE, library listing/deletion, and deduplication by MBID/path/hash.
7. **M7 Analytics and packaging:** ⏳ Planned - summary endpoints, Docker image (ffmpeg, yt-dlp, Deno), docs, and CI.
8. **M8 UI:** ⏳ Planned - minimal static frontend (Search, Activity, Library, Settings).

### Recommended implementation order

1. Complete one single-track pipeline end to end using only an acquisition source the user is authorized to access: scorer correctness, approved-candidate behavior, acquisition, MP3 conversion, tagging, safe library path, duplicate detection, and persisted `LibraryFile`.
2. Verify job lifecycle and failure recovery for that vertical slice with integration tests; do not mark a job `Done` until the file and database record are successfully committed.
3. Add bulk enqueue/import workflows, starting with public Spotify playlists, then album and artist top-N flows.
4. Add SSE/activity and library list/delete endpoints, followed by the minimal UI that exposes Search, Activity, Library, and Settings.
5. Prioritize security, configuration, and deployment packaging needed for the chosen target; defer analytics and other secondary features until the core workflow is dependable.

## 15. Open questions (ask the owner before assuming)

- **Deployment target:** bare metal, Docker on a NAS, or both?

Owner decisions already recorded:

- Single-user service.
- MP3 is the intended default output format.
- Use yt-dlp as the intended acquisition provider, subject to lawful use and service terms.
- `NeedsReview` jobs park without blocking later queued jobs.
- Queue ordering is FIFO; first queue-management scope is observing/managing jobs, not batch enqueue.
- Use EF Core migrations for future schema changes; do not use `EnsureCreated` for application startup or database upgrades.

## 16. Instructions to the AI assistant

- Work milestone by milestone. Don't scaffold everything at once.
- Before writing code that touches paths, processes or external APIs, re-read sections 8.2, 8.4 and 11.
- Write or update unit tests with each change, especially for `IMatchScorer`, filename sanitization and path resolution.
- If a requirement here conflicts with an API's actual current behavior (e.g. Spotify endpoint availability), say so and propose an alternative instead of guessing.
- Keep the acquisition provider isolated, as described in section 3.
