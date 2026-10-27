# musicsearcher

A self-hosted .NET backend for music library management, ripping, and metadata tagging. 
Transforms audio from various sources into organized, tagged files for use with media 
servers like Navidrome or Jellyfin.

## Project Idea

musicsearcher implements the core idea from **FreeListen** (by [Alextheguy1](https://github.com/Alextheguy1)), 
a Node/Express project for music search and download, reimplementing it in **C# / .NET** with 
significant improvements focused on correctness, maintainability, and proper multi-artist 
attribution.

The original FreeListen took the first `ytsearch1` result without scoring or review, had an 
in-memory queue, and created composite artist names (e.g., "A+B") for multi-artist tracks. 
This project fixes those issues with:

- **Multi-candidate search + scoring** - evaluates up to 5 candidates per track using duration,
  title similarity, and artist name matching
- **Persisted queue** - job queue in SQLite, resumable after restart
- **Server-side MusicBrainz client** - with global 1 request/second rate limiter and proper 
  User-Agent headers
- **SSE progress** - real-time job progress streaming
- **Proper multi-artist attribution** - uses `ArtistCredit` records so tracks appear in ALL 
  artist indexes (fixes the "composite artist C (a+b)" bug)
- **TagLibSharp integration** - tags files with MBIDs, cover art, and full metadata
- **Docker packaging** - includes ffmpeg, yt-dlp, and Deno

## What It Does

1. **Search** music by song, album, or artist using MusicBrainz canonical metadata
2. **Import** from public Spotify playlists (Client Credentials flow, no user OAuth)
3. **Find audio sources** and download via yt-dlp
4. **Convert/transcode** audio using ffmpeg (MP3, FLAC, Opus, M4A)
5. **Tag** files with complete MusicBrainz metadata (MBIDs, cover art, artist credits)
6. **Organize** library in server-friendly layout: `{LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}`
7. **Track jobs** with persistence, resumability, and progress events
8. **Provide analytics** on library completeness, top artists, success/failure rates

## Technology Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 8+ (LTS), C# with nullable reference types |
| Web | ASP.NET Core minimal APIs, HTTPS only |
| Persistence | EF Core + SQLite (Postgres-compatible schema) |
| Background work | `BackgroundService` + `System.Threading.Channels`, jobs in SQLite |
| Realtime progress | Server-Sent Events (SSE) |
| Audio download | yt-dlp via custom acquisition provider |
| Transcoding | ffmpeg (external binary, required) |
| Tagging | TagLibSharp (with MBIDs and embedded cover) |
| HTTP clients | `IHttpClientFactory`, Polly resilience handlers |
| Rate limiting | `System.Threading.RateLimiting` |
| Logging | `ILogger`, structured templates |
| Tests | xUnit, FluentAssertions |
| Packaging | Docker image (ffmpeg, yt-dlp, Deno) |

## Architecture

```
Web UI / API ────────► ASP.NET Core API ────────► Job Queue (SQLite)
                                                 │
                                                 ▼
                                          Worker (BackgroundService)
                                                 │
        ┌───────────────────────────────────────┼─────────────────────────────────────┐
        │ MusicBrainz          ListenBrainz     Cover Art Archive     Spotify (public) │
        └───────────────────────────────────────┴─────────────────────────────────────┘
```

## Milestones Status

| Milestone | Description | Status |
|---|---|---|
| **M1 Skeleton** | Solution layout, DI, EF Core + SQLite, health endpoint, auth setup | ✅ Completed |
| **M2 Metadata** | MusicBrainz client, search endpoints, cover art | ⏳ Not yet started |
| **M3 Pipeline** | Job entity, persisted queue, worker, yt-dlp provider, ffmpeg transcode, TagLibSharp tagging, LibraryPathResolver | ✅ Completed |
| **M4 Matching** | Scorer with unit tests, review state, approve/cancel endpoints | ⏳ Not yet started |
| **M5 Playlists** | Spotify public playlist import, ListenBrainz top recordings | ⏳ Not yet started |
| **M6 Realtime** | SSE, library listing/deletion, dedupe by hash and MBID | ⏳ Not yet started |
| **M7 Analytics** | Summary endpoints, Dockerfile, docs, CI | ⏳ Not yet started |
| **M8 UI** | Minimal static frontend (Search, Activity, Library, Settings) | ⏳ Not yet started |

## Key Features

- **Multi-artist support** via `ArtistCredit` records - tracks appear in ALL artist indexes, 
  avoiding the "composite artist C (a+b)" bug
- **Multi-candidate scoring** - evaluates up to 5 download candidates per track using duration, 
  title similarity, and artist name matching
- **Persisted job queue** - SQLite-backed, resumable after restart with full progress tracking
- **Path safety** - all user-supplied path components sanitized and verified against library root
- **No shell injection** - uses `ProcessStartInfo.ArgumentList`, never string concatenation for commands
- **Rate-limited external API calls** - 1 request/second global limiter for MusicBrainz
- **Configurable library layout** - `{LibraryRoot}/{Artist}/{Album}/{NN} - {Title}.{ext}` or singles mode
- **Dedup by MBID → path → SHA-256** - prevents duplicate files
- **SSE progress streaming** - real-time job progress from the worker
- **Full configuration** via `appsettings.json` and environment variables

## Configuration

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

## Legal Disclaimer

Downloading audio from YouTube or SoundCloud may violate their Terms of Service and may 
infringe copyright depending on jurisdiction. This project is for **personal use only**.

The acquisition step sits behind an interface (`IAcquisitionProvider`). The default 
implementation uses yt-dlp. Users are responsible for complying with applicable laws and 
Terms of Service.

## Development

```
# Build all projects
dotnet build

# Run tests (compile only, execution requires .NET 8 runtime)
dotnet test

# Run the API project
dotnet run --project MusicSync.Api
```

## Credits

**Design inspiration and reference project:** [FreeListen](https://github.com/Alextheguy1/FreeListen) 
by [Alextheguy1](https://github.com/Alextheguy1) - a Node/Express implementation that 
inspired the core idea of this project.

This project reimplements the same concept in **C# / .NET** with the improvements listed 
in [PROJECT_CONTEXT.md section 9](PROJECT_CONTEXT.md#9-improvements-over-the-reference-project):

| Reference (FreeListen) | This project |
|---|---|
| Takes first `ytsearch1` result | Multi-candidate search + scoring + review state |
| In-memory queue and activity log | Persisted queue and history in SQLite |
| MusicBrainz called from browser | Server-side client with 1 rps limiter, User-Agent, caching |
| UI polls for activity | SSE/SignalR push |
| Spotify embed scraping / unofficial lib | Official API (Client Credentials), fallback optional |
| ffmpeg `-metadata` for tagging | TagLibSharp, plus MBIDs in tags |
| Dedupe by sanitized title | Dedup by MBID, then path, then SHA-256 |
| **Creates composite artist C (a+b)** | **Proper multi-artist attribution via ArtistCredit records** |

---

*Working name: MusicSync - rename freely as needed.*
