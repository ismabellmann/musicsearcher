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
| Background work | `BackgroundService` + `System.Threading.Channels` |
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
| Core project and domain foundations | ✅ In progress |
| SQLite persistence and entity model | ✅ In progress |
| Background worker and acquisition flow | ✅ In progress |
| Candidate matching and scoring | ✅ Implemented |
| MusicBrainz integration | 🚧 In progress |
| Playlist imports | ⏳ Planned |
| Realtime progress and library endpoints | ⏳ Planned |
| Analytics and container packaging | ⏳ Planned |
| Web UI | ⏳ Planned |

## Configuration

Settings can be supplied through `appsettings.json` or environment variables:

```text
Library__Root=/music
Audio__Format=mp3                 # mp3|flac|opus|m4a
Audio__Quality=0
Matching__AutoAcceptThreshold=0.80
Matching__ReviewThreshold=0.50
Matching__DurationToleranceSeconds=5
Worker__Concurrency=1
MusicBrainz__UserAgent=MusicSync/0.1 (you@example.com)
ListenBrainz__Token=              # optional
Spotify__ClientId=                # optional
Spotify__ClientSecret=            # optional
Auth__Disable=false               # use only behind a trusted proxy
```

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
