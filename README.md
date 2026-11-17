# MusicSync

MusicSync is a self-hosted .NET backend for discovering, importing, and organizing music. It brings together metadata services, audio acquisition, transcoding, tagging, and library management for personal media servers such as Navidrome and Jellyfin.

> **Project status:** MusicSync is an active work in progress. Some planned API, playlist, analytics, and UI features are still being developed.

## Inspiration

MusicSync was inspired by [FreeListen](https://github.com/Alextheguy1/FreeListen), created by [Alextheguy1](https://github.com/Alextheguy1). FreeListen established the original direction and demonstrated how a simple music search and download experience could be built with a web application.

This project explores the same broader idea in C# and .NET, with an emphasis on maintainable service boundaries, persistent jobs, canonical metadata, and a foundation that can grow over time. The projects serve different technical goals and are not intended to be direct replacements for one another.

Thank you to the FreeListen project and its contributors for the inspiration.

## What MusicSync Does

The planned workflow is:

1. Search for music using canonical MusicBrainz metadata.
2. Import tracks from supported public playlists.
3. Find candidate audio sources through an acquisition provider.
4. Score candidates using title, artist, and duration information.
5. Download and transcode audio with `yt-dlp` and `ffmpeg`.
6. Apply metadata, MBIDs, artist credits, and cover art.
7. Organize files in a media-server-friendly library layout.
8. Track jobs with persistence, resumability, and progress reporting.

## Highlights

- Canonical metadata through MusicBrainz.
- Multi-candidate matching with configurable scoring thresholds.
- Persisted, resumable download jobs backed by SQLite.
- Multi-artist attribution through artist credits.
- Safe library path resolution and filename sanitization.
- SHA-256 and metadata-based deduplication foundations.
- Server-Sent Events (SSE) planned for live progress updates.
- External integrations kept behind interfaces where practical.
- Docker packaging is available for TrueNAS through GHCR (`linux/amd64`).

## Technology

| Area | Technology |
|---|---|
| Runtime | .NET 8 (with .NET 10 upgrade work tracked separately) |
| Language | C# with nullable reference types |
| Web API | ASP.NET Core |
| Persistence | Entity Framework Core + SQLite |
| Background work | `BackgroundService` polling persisted SQLite jobs |
| Audio acquisition | `yt-dlp` through an acquisition provider |
| Transcoding | `ffmpeg` |
| Tagging | TagLibSharp |
| Metadata | MusicBrainz, ListenBrainz, and Spotify integrations |
| Tests | xUnit |

## Architecture

```text
Web client / API
        │
        ▼
ASP.NET Core API ───────► Persistent job queue (SQLite)
                              │
                              ▼
                      Background worker
                              │
        ┌─────────────────────┼─────────────────────┐
        │ MusicBrainz         │ Spotify / playlists  │
        │ ListenBrainz        │ Audio providers      │
        └─────────────────────┴─────────────────────┘
```

## Project Layout

```text
MusicSync/
├── Core/            # Domain models, interfaces, matching, and path logic
├── Infrastructure/  # Persistence models and external-service infrastructure
├── Worker/          # Background processing and acquisition workflow
└── ../MusicSync.Api/ # HTTP API and controllers
tests/
├── MusicSync.Core.Tests/
└── MusicSync.Infrastructure.Tests/
```

## Progress

| Area | Status |
|---|---|
| Core project and domain foundations | ✅ Implemented |
| SQLite persistence and entity model (EF Core migrations, artist credits) | 🚧 In progress |
| Background worker and acquisition flow | 🚧 In progress (single-track pipeline with automatic retries; candidate review; recovery on restart) |
| Candidate matching and scoring | 🚧 In progress (scorer implemented; candidate selection pending) |
| MusicBrainz, ListenBrainz, and Spotify parsing | ✅ Implemented with tests |
| Search and playlist API integration | 🚧 In progress |
| Playlist imports and track mapping | 🚧 In progress (public preview and enqueue workflow) |
| Realtime progress and library endpoints | ⏳ Planned |
| Analytics and container packaging | ⏳ Planned |
| Web UI | ⏳ Planned |

## Configuration

Settings come from environment variables or `appsettings.json`. A double underscore
(`__`) separates nested sections, so `Library__Root` is the `Library:Root` setting.
Relative paths resolve against the API content root. Defaults are shown below.

### Server and storage

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_URLS` | `http://localhost:5000` | Address and port to listen on. The container image uses `http://+:28080`, so it does not clash with other services on the host. |
| `Data__DatabasePath` | `data/musicsync.db` (Production)<br>`config/musicsync.db` (Development) | SQLite database file. Put it on persistent storage. |
| `Config__Dir` | `data/config` (Production)<br>`config` (Development) | Persistent folder for the cookie-protection keys. Keep it across restarts, or every session is signed out. |

For local development, `dotnet run --project MusicSync.Api` uses the `MusicSync.Api`
launch profile, which sets `ASPNETCORE_ENVIRONMENT=Development`. In that environment the
database and keys default to the `config/` folder at the repository root; its contents are
ignored by git. This is a development convenience only. In production, set
`Config__Dir` and `Data__DatabasePath` explicitly to persistent paths, for example
`/config` and `/config/musicsync.db` in the container.

### Sign-in

| Variable | Default | Purpose |
|---|---|---|
| `Auth__SecureCookies` | `false` | Adds the `Secure` flag to the session cookie, so browsers send it only over HTTPS. Keep `false` when serving plain HTTP, or the browser will not keep the login. |
| `Auth__SessionHours` | `168` | Session lifetime in hours, from 1 to 720. |

### Library and acquisition

| Variable | Default | Purpose |
|---|---|---|
| `Library__Root` | `tracks/` at the repository root | Folder where finished MP3 files are stored. In Docker, use `/music`. |
| `Library__StagingDirectory` | `data/staging` | Temporary work folder for downloads. |
| `Library__YouTubeFallback` | `true` | Searches YouTube when MusicBrainz has no candidate matching the file's artist during library import. Results are only listed, never downloaded. |
| `Library__YouTubeMaxCandidates` | `5` | YouTube results compared per file during library import. |
| `Library__EmbedCoverOnImport` | `true` | Embeds the release-group front cover into accepted library files. Existing text tags are not changed. |
| `Acquisition__LocalSourceDirectory` | `tracks/` | Folder read by the local-file provider. |
| `Audio__Format` | `mp3` | Output format. Only `mp3` is supported now. |
| `Audio__Quality` | `0` | ffmpeg quality value for MP3 (0 is the highest quality). |
| `Tools__Ffmpeg` | `ffmpeg` | ffmpeg executable name or full path. |
| `Tools__Ffprobe` | `ffprobe` | ffprobe executable name or full path. |
| `Tools__YtDlp` | `yt-dlp` | yt-dlp executable name or full path. |

### Metadata services

| Variable | Default | Purpose |
|---|---|---|
| `MusicBrainz__UserAgent` | `MusicSync/0.1 (https://github.com/ismabellmann/musicsearcher)` | Identifying User-Agent for MusicBrainz, in the form `Name/Version (contact)`. MusicBrainz needs no account or key. |
| `ListenBrainz__Token` | empty | Optional ListenBrainz token. It is read but not yet sent with any request. |
| `Playlist__MaxTracks` | `100` | Maximum public Spotify tracks processed in one preview. |
| `Playlist__MaxCandidates` | `10` | Maximum MusicBrainz candidates stored per playlist track. |
| `CoverArt__Enabled` | `true` | Fetch the release-group front cover from the Cover Art Archive when tagging a newly acquired track. |
| `CoverArt__MaxBytes` | `5000000` | Largest cover image accepted, in bytes. |
| `CoverArt__TimeoutSeconds` | `15` | Cover Art Archive request timeout, in seconds. |

### Matching and worker

| Variable | Default | Purpose |
|---|---|---|
| `Matching__AutoAcceptThreshold` | `0.80` | Score from 0 to 1 at which a candidate is accepted automatically. |
| `Matching__ReviewThreshold` | `0.50` | Score from 0 to 1 at which a candidate waits for review. Must not be above the auto-accept value. |
| `Matching__DurationToleranceSeconds` | `5` | Length difference, in seconds, that still counts as a duration match. |
| `Worker__Concurrency` | `1` | Jobs processed at once. Only `1` is supported now. |
| `Worker__MaxAutomaticRetries` | `2` | Retries for transient failures (rate limits, network errors, timeouts) before a job fails. |
| `Worker__RetryBaseDelaySeconds` | `30` | First retry delay. Each later retry doubles it, up to 10 minutes. |

Invalid values stop the application at startup with a message that names the setting.

### HTTPS (optional)

The default is plain HTTP, which works on a local network. To serve HTTPS directly, give
Kestrel a certificate and set the cookie flag:

```text
ASPNETCORE_URLS=https://+:8443
ASPNETCORE_Kestrel__Certificates__Default__Path=/config/certificate.pfx
ASPNETCORE_Kestrel__Certificates__Default__Password=<certificate password>
Auth__SecureCookies=true
```

Or let TrueNAS or a reverse proxy terminate TLS and keep the container on HTTP.
The HTTPS settings above have not been tested end to end yet.

### Docker on TrueNAS

The published image is `ghcr.io/ismabellmann/musicsearcher:latest`. It listens on
container port `28080` and runs as UID/GID `568:568`, the TrueNAS apps user. The image
contains ffmpeg/ffprobe, Deno, and pinned yt-dlp `2026.10.26`.

Create the persistent config directory on the NAS before starting the app:

```bash
mkdir -p /mnt/tank/Servidores/musicsync
chown -R 568:568 /mnt/tank/Servidores/musicsync
```

The music dataset must also grant UID/GID 568 read and write access because downloads,
embedded covers, and the same-filesystem staging directory are written below `/music`.
The recommended mounts and port are:

| Container | Host |
|---|---|
| `/music` | `/mnt/tank/ftp/musica` |
| `/config` | `/mnt/tank/Servidores/musicsync` |
| `28080` | `28080` |

The repository includes [`compose.yaml`](compose.yaml) with these values. For a TrueNAS
custom app, use the same image, port, user, environment variables, and mounts. On first
start, open `http://NAS_IP:28080` and create the single service account. Set the
`MusicBrainz__UserAgent` environment variable to a contact address or URL of your own.

The database and cookie-protection keys live in `/config`; back up that directory before
upgrades. The GHCR package must be public for an unauthenticated TrueNAS pull, or configure
registry credentials in the app. To update, pull the new image and recreate the container;
the database is upgraded by the application at startup.

The Docker image is currently built and published for `linux/amd64` by GitHub Actions.
yt-dlp is intentionally pinned; update its version and checksum in `Dockerfile`, verify the
image, then publish a new image tag before upgrading the NAS.

### First run and sign-in

Until the first account exists, every route except `/api/health`, `/api/auth/status`,
and `/api/auth/setup` returns `403`. Open the app and create the account. The username
is 3 to 50 characters (letters, digits, `.`, `-`, `_`). The password is 12 to 256
characters. Setup works only once.

- The password is stored as a salted PBKDF2 hash (ASP.NET `PasswordHasher`). It cannot be read back from the database, so it is not stored in reversible form.
- The session is an `HttpOnly`, `SameSite=Strict` cookie, valid for `Auth__SessionHours`.
- Login and first-run setup are limited to 5 requests per five-minute window per client IP.
- `POST /api/auth/logout-all` changes the account security stamp and invalidates all existing sessions.
- Endpoints: `GET /api/auth/status`, `POST /api/auth/setup`, `POST /api/auth/login`, `POST /api/auth/logout`, `POST /api/auth/password`.

Known limits: the ListenBrainz token is read from the environment and not stored in the
database; settings management and encrypted secret storage are not implemented yet.

The API applies pending EF Core migrations on startup. New schema changes should
be added as migrations in `MusicSync/Infrastructure/Migrations`; do not use
`EnsureCreated` for application startup or upgrades. Install the matching EF
tool and create a migration from the repository root:

```bash
dotnet tool install --global dotnet-ef --version 8.0.0
dotnet ef migrations add MigrationName \
  --project MusicSync/Infrastructure/MusicSync.Infrastructure.csproj \
  --context MusicSyncDbContext \
  --output-dir Migrations
```

For databases created by the previous `EnsureCreated` setup, startup baselines
the database only if its tables and columns match the known schema exactly;
data is retained. An unknown or partially changed schema causes startup to fail
with an actionable error instead of attempting a destructive recreation.

## Library import

Existing files in the library folder can be identified without downloading anything.
The folder layout tells the app the artist and title: `Artist/Title.mp3` or
`Artist/Album/NN - Title.mp3`. Other layouts are reported as unrecognized.

1. `POST /api/library/scan` lists the new files as `Pending`. Files already known are skipped.
2. `POST /api/library/imports/identify` searches MusicBrainz for `Title - Artist` and stores up
   to 10 candidates in total. If no candidate matches the file's artist, it also searches YouTube with
   yt-dlp (`--skip-download`, search results only) and adds up to `Library__YouTubeMaxCandidates`
   videos. Each candidate says whether it came from `musicbrainz` or `youtube` and includes its
   `sourceUrl`. Items become `Identified` when a candidate is above the review threshold and
   matches the artist, otherwise `NoMatch`.
   Pass `{"importIds": [..]}` to choose files; the default is every `Pending` file.
3. `GET /api/library/imports/{id}` shows the candidates. Choose one with
   `POST /api/library/imports/{id}/accept` and `{"candidateId": n}`. For a YouTube match, the
   artist and title are read from the video title (`Artist - Title`); when the title has no
   separator, the folder values are stored instead. The video URL is saved as the track origin.
   For a MusicBrainz candidate, the front cover of its release group is fetched from the Cover
   Art Archive and embedded into the existing file. The rest of the file's tags are left
   unchanged, and a missing or unreachable cover does not block the import. A recording or video
   that is already in the library is refused with `409`. The file's SHA-256 is checked before the
   cover is embedded; if the same bytes are already stored under another recording, the accept is
   refused with `409` and the file is left untouched.
4. `POST /api/library/imports/{id}/reject` discards a file.
5. `PUT /api/library/imports/{id}/path` with `{"relativePath": "..."}` changes the stored path.
   The file is not moved. The new path must stay inside the library and point to an existing file.
6. `POST /api/library/covers` adds covers to files that were imported before cover embedding was
   enabled. It only processes `Imported` files whose track has a release-group MBID, embeds the
   front cover without changing text tags, and refreshes the stored `Sha256`/`SizeBytes`. Pass
   `{"importIds": [..]}` to target specific imports; the default is every eligible import. The
   response reports `scanned`, `embedded`, `skipped`, `failed`. A missing cover counts as
   `skipped`; a missing file or write error counts as `failed` and does not stop the batch.

Limits: the search uses the artist from the folder name, so a wrong folder gives wrong
candidates. A YouTube-only track gets a stable identifier derived from the video ID instead of
a real MusicBrainz recording ID. Matching is title, artist and duration only; there is no audio
fingerprint. Identification runs inside the request, and yt-dlp adds a few seconds per fallback
file, so send `importIds` in chunks for large libraries. Tags are not read yet; only cover art is
written back into imported files.

## API responses

Successful and validation-error responses include an HTTP status, a Spanish
`msg`, and a `data` field:

```json
{
  "status": 202,
  "msg": "Canción añadida a la cola exitosamente.",
  "data": {
    "jobId": 1,
    "jobStatus": "Queued"
  }
}
```

Submit a track for acquisition with `POST /api/downloads`, sending its
MusicBrainz recording ID, title, artist, album, release-group ID, year, and
duration in milliseconds. The hosted worker reads queued jobs from SQLite,
selects and scores a candidate, then either accepts it automatically or parks
the job in `NeedsReview`. After approval it downloads the source, transcodes it
to MP3 with ffmpeg, writes ID3 tags and an optional Cover Art Archive front
cover with TagLibSharp, stores the file under `Library__Root`, records a
`LibraryFile`, and marks the job `Done`. A missing cover or an unreachable
archive never fails the job. Tags are written only for newly acquired files;
files already in the library are not rewritten.

Output paths use `{Artist}/{Title}.mp3` for singles and
`{Artist}/{Album}/{NN} - {Title}.mp3` when album metadata is present. Active jobs can be
cancelled cooperatively; provider downloads, transcoding, staging files, and temporary
provider directories are cleaned up before the job becomes `Cancelled`.

Duplicate handling:

- If the requested recording already has a `LibraryFile`, the request still
  returns `202` and creates a job. The worker marks that job `Done` with the
  note `AlreadyInLibrary` and the existing path in `outputPath`, without calling
  the acquisition provider.
- If the target path already exists on disk but has no `LibraryFile`, the job
  is marked `Done` with `AlreadyExists`.
- If the same recording already has an active job (`Queued`, `Resolving`,
  `Searching`, `NeedsReview`, `Approved`, `Downloading`, `Tagging`, or
  `ReadyForTagging`), the request returns `409 Conflict` and no second job is
  created.

Playlist import uses public Spotify pages and does not require Spotify credentials:

1. `POST /api/playlists/preview` with `{"url":"https://open.spotify.com/playlist/..."}`
   persists the ordered track list and MusicBrainz candidates. The preview is limited by
   `Playlist__MaxTracks` and stores up to `Playlist__MaxCandidates` candidates per item.
2. `GET /api/playlists/{id}` returns the preview and item statuses. Automatic matches are
   marked `Matched`; ambiguous and missing matches remain reviewable or `NoMatch`.
3. `POST /api/playlists/{id}/enqueue` with `itemIds` and optional `selections` enqueues only
   confirmed candidates. Repeating the request does not create duplicate jobs.
4. `POST /api/playlists/album/{mbid}/preview` previews every recording in a MusicBrainz album.
5. `POST /api/playlists/artist/{mbid}/preview?artistName=...&count=10` previews ListenBrainz's
   top recordings for an artist and maps them back to MusicBrainz recordings.

Queue management endpoints:

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/downloads?status=Queued&page=1&pageSize=50` | List jobs; status is optional, page size is limited to 100. Queued results are FIFO and include queue positions. |
| `GET` | `/api/downloads/{id}` | Get a job and its current status. |
| `POST` | `/api/downloads/{id}/approve` | Approve a `NeedsReview` candidate; the worker then downloads, tags, and stores it. |
| `POST` | `/api/downloads/{id}/cancel` | Cancel queued, review, approved, resolving, searching, downloading, tagging, or ready-for-tagging jobs. Terminal jobs return `409 Conflict`; active provider work is cancelled cooperatively. |
| `POST` | `/api/downloads/{id}/retry` | Requeue a failed job, preserving its attempt count and clearing its prior error and candidate. |
| `GET` | `/api/events?jobId=123&after=42` | Authenticated SSE stream of persisted job events; `Last-Event-ID` is also supported. |

Library management endpoints:

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/library?q=&origin=imported&page=1&pageSize=50` | Paginated library files with title, artist, album, format, size, and origin filters. |
| `GET` | `/api/library/{id}` | Full file and track metadata, including recording MBID, hash, source, and ordered artist credits. |
| `DELETE` | `/api/library/{id}` | Deletes the contained file, removes its `LibraryFile` row, resets an imported record to `Pending`, and prunes empty directories. |

These endpoints use the same `{ "status", "msg", "data" }` response envelope.
Manual retries currently have no configured limit; automatic transient retry
and backoff are not implemented. Approval preserves the candidate source and
score; audio acquisition remains a later pipeline step.

## Web UI

The application serves a lightweight dependency-free frontend from `/`. It uses vanilla
HTML, CSS, and JavaScript with no framework, external assets, web fonts, or build step.
It includes Search, Activity/SSE, Library, Playlists, Settings, setup/login handling, and
responsive desktop/mobile layouts. The theme button cycles through system, light, and dark
themes; the choice is stored locally in the browser.

## Development

The commands below should be run from the repository root. On immutable systems such as Bazzite, a development container or Distrobox is a convenient way to provide the required .NET runtime and SDK.

```bash
# Restore dependencies
dotnet restore

# Build an individual project
dotnet build MusicSync/Core/MusicSync.Core.csproj

# Run tests
dotnet test tests/MusicSync.Core.Tests/MusicSync.Core.Tests.csproj
dotnet test tests/MusicSync.Infrastructure.Tests/MusicSync.Infrastructure.Tests.csproj

# Run the API when its current dependencies are available
dotnet run --project MusicSync.Api/MusicSync.Api.csproj
```

## Legal Notice

Audio availability and downloading may be subject to the terms of service of external providers and to copyright law. MusicSync is intended for lawful personal use. Users are responsible for complying with applicable laws and service terms.

The acquisition layer is defined behind an interface so that users can choose an implementation appropriate for their environment and use case.

## Acknowledgements

Special thanks to [Alextheguy1](https://github.com/Alextheguy1) and the [FreeListen](https://github.com/Alextheguy1/FreeListen) project for the original inspiration.

MusicSync is an independent project that builds on that inspiration while exploring its own architecture and implementation choices.
