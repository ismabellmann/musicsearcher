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
- Docker packaging planned for the complete audio toolchain.

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
| Playlist imports and track mapping | ⏳ Planned |
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
| `ASPNETCORE_URLS` | `http://localhost:5000` | Address and port to listen on. Use `http://+:8080` inside a container. |
| `Data__DatabasePath` | `data/musicsync.db` | SQLite database file. Put it on persistent storage. |
| `Config__Dir` | `data/config` | Persistent folder for the cookie-protection keys. Keep it across restarts, or every session is signed out. |

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
| `Spotify__ClientId` | empty | Optional Spotify app client ID, used by the current playlist client. |
| `Spotify__ClientSecret` | empty | Optional Spotify app client secret, used by the current playlist client. |

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

### First run and sign-in

Until the first account exists, every route except `/api/health`, `/api/auth/status`,
and `/api/auth/setup` returns `403`. Open the app and create the account. The username
is 3 to 50 characters (letters, digits, `.`, `-`, `_`). The password is 12 to 256
characters. Setup works only once.

- The password is stored as a salted PBKDF2 hash (ASP.NET `PasswordHasher`). It cannot be read back from the database, so it is not stored in reversible form.
- The session is an `HttpOnly`, `SameSite=Strict` cookie, valid for `Auth__SessionHours`.
- Endpoints: `GET /api/auth/status`, `POST /api/auth/setup`, `POST /api/auth/login`, `POST /api/auth/logout`, `POST /api/auth/password`.

Known limits: login attempts are not rate-limited yet, logout and password change do not
revoke a session that was copied elsewhere before it expired, and the ListenBrainz and
Spotify secrets are read from environment variables, not stored in the database.

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
MusicBrainz recording ID, title, artist, and duration in milliseconds. The
hosted worker reads queued jobs from SQLite, selects and scores a candidate,
then persists its source and score with status `ReadyForTagging`. Actual audio
download and TagLibSharp tagging are intentionally outside this pipeline slice.

Queue management endpoints:

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/downloads?status=Queued&page=1&pageSize=50` | List jobs; status is optional, page size is limited to 100. Queued results are FIFO and include queue positions. |
| `GET` | `/api/downloads/{id}` | Get a job and its current status. |
| `POST` | `/api/downloads/{id}/approve` | Approve a `NeedsReview` candidate; records status `Approved` but does not start a download. |
| `POST` | `/api/downloads/{id}/cancel` | Cancel a queued or review-pending job. Active or terminal jobs return `409 Conflict`; active provider work is not interrupted. |
| `POST` | `/api/downloads/{id}/retry` | Requeue a failed job, preserving its attempt count and clearing its prior error and candidate. |

These endpoints use the same `{ "status", "msg", "data" }` response envelope.
Manual retries currently have no configured limit; automatic transient retry
and backoff are not implemented. Approval preserves the candidate source and
score; audio acquisition remains a later pipeline step.

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
