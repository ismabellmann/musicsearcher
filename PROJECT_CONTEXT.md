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
This project reimplements the same idea in **C# / .NET** with improvements listed in section 9.

## 2. Non-goals

- No multi-tenant SaaS, no public-internet exposure by default.
- No streaming/playback. Output is files on disk, consumed by Navidrome, Jellyfin, etc.
- No Spotify user-account integration (no per-user tokens, no private playlists).
- No DRM circumvention.

## 3. Legal / ToS note (design constraint)

Downloading audio from YouTube or SoundCloud violates their Terms of Service and may infringe copyright depending on jurisdiction. The project is for **personal use**.

Therefore:

- The acquisition step MUST sit behind an interface (`IAcquisitionProvider`). Nothing outside that provider may reference YouTube, SoundCloud or yt-dlp.
- Ship a `YtDlpAcquisitionProvider` as the default implementation, and design for additional providers (local import, Bandcamp downloads the user owns, Internet Archive, Jamendo, Free Music Archive).
- The README MUST include a short disclaimer that users are responsible for complying with applicable laws and ToS.

## 4. Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 8+ (LTS), C# with nullable reference types enabled |
| Web | ASP.NET Core minimal APIs, HTTPS only |
| Persistence | EF Core + SQLite (Postgres-compatible schema; avoid SQLite-only features) |
| Background work | `BackgroundService` + `System.Threading.Channels`, jobs persisted in DB |
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

`DownloadJob.Status`: `Queued`, `Resolving`, `Searching`, `NeedsReview`, `Downloading`, `Tagging`, `Done`, `Failed`, `Cancelled`.

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

### 8.5 Queue / worker
- Endpoints enqueue and return immediately (`202 Accepted` + job id).
- A single worker (configurable concurrency, default **1**) drains the queue. Throttling avoids provider rate limits.
- Jobs are **persisted** and **resumable** after restart (re-enqueue `Queued`/`Resolving`/`Searching`/`Downloading` on startup).
- Retry transient failures with exponential backoff (max 2 retries by default).
- Idempotent: if the target file already exists (same MBID or same sanitized `Artist/Album/Title`), skip and mark `Done` with note `AlreadyExists`.
- Publish progress over SSE.

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

| Reference (FreeListen) | This project |
|---|---|
| Takes first `ytsearch1` result | Multi-candidate search + scoring + review state |
| In-memory queue and activity log | Persisted queue and history in SQLite |
| MusicBrainz called from browser | Server-side client with global 1 rps limiter, `User-Agent`, caching |
| UI polls for activity | SSE/SignalR push |
| Spotify embed scraping / unofficial lib | Official API (Client Credentials), fallback optional |
| ffmpeg `-metadata` for tagging | TagLibSharp, plus MBIDs in tags |
| Dedupe by sanitized title | Dedupe by MBID, then path, then SHA-256 |
| **Creates composite artist C (a+b) for multi-artist tracks** | **Proper multi-artist attribution via ArtistCredit records, track appears in ALL artist indexes** |

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
| POST | `/downloads/{id}/approve` | Approve a `NeedsReview` candidate |
| POST | `/downloads/{id}/cancel` | Cancel a job |
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

1. **M1 Skeleton:** solution layout, DI, EF Core + SQLite, health endpoint, auth setup/login. ✅ Partially - solution layout and project structure implemented, Core library with domain models including multi-artist TrackInfo.
2. **M2 Metadata:** MusicBrainz client (rate limiter, UA, cache) + search endpoints + cover art. ⏳ Not yet started.
3. **M3 Pipeline:** job entity, persisted queue, worker, `YtDlpAcquisitionProvider`, ffmpeg transcode, TagLibSharp tagging, **library path resolver with traversal tests**. ✅ Completed - `LibraryPathResolver` with multi-artist support and path sanitization per section 11.
4. **M4 Matching:** scorer with unit tests, review state, approve/cancel endpoints. ⏳ Not yet started.
5. **M5 Playlists and top-N:** Spotify public playlist import, ListenBrainz top recordings.
6. **M6 Realtime and library:** SSE, library listing/deletion, dedupe by hash and MBID.
7. **M7 Analytics and packaging:** summary endpoints, Dockerfile (ffmpeg, yt-dlp, Deno), docs, CI.
8. **M8 UI:** minimal static frontend (Search, Activity, Library, Settings).

## 15. Open questions (ask the owner before assuming)

- Single-user only, or eventually multi-user?
- Which output format should be the default?
- Should `NeedsReview` block the queue or just park the item?
- Should extra acquisition providers (local import, Bandcamp, Internet Archive) be in scope for v1?
- Target deployment: bare metal, Docker on a NAS, or both?

## 16. Instructions to the AI assistant

- Work milestone by milestone. Don't scaffold everything at once.
- Before writing code that touches paths, processes or external APIs, re-read sections 8.2, 8.4 and 11.
- Write or update unit tests with each change, especially for `IMatchScorer`, filename sanitization and path resolution.
- If a requirement here conflicts with an API's actual current behavior (e.g. Spotify endpoint availability), say so and propose an alternative instead of guessing.
- Keep the acquisition provider isolated, as described in section 3.
