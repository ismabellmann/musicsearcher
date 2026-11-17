using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MusicSync.Core;

namespace MusicSync.Core.Interfaces;

/// <summary>
/// Interface for MusicBrainz canonical metadata provider.
/// Handles server-side search, lookup, and metadata retrieval.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Search for tracks matching the given query.
    /// Supports artist:name style narrowing.
    /// </summary>
    /// <param name="query">Search query string</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<TrackInfo>> SearchTracksAsync(string query, CancellationToken ct);

    /// <summary>
    /// Search for albums matching the given query.
    /// </summary>
    /// <param name="query">Search query string</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<TrackInfo>> SearchAlbumsAsync(string query, CancellationToken ct);

    /// <summary>
    /// Search for artists matching the given query.
    /// </summary>
    /// <param name="query">Search query string</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<TrackInfo>> SearchArtistsAsync(string query, CancellationToken ct);

    /// <summary>
    /// Get detailed album information by release group MBID.
    /// </summary>
    /// <param name="releaseGroupMbid">MusicBrainz release group MBID</param>
    /// <param name="ct">Cancellation token</param>
    Task<AlbumDetail?> GetAlbumAsync(Guid releaseGroupMbid, CancellationToken ct);

    /// <summary>
    /// Look up a track by its ISRC code.
    /// </summary>
    /// <param name="isrc">ISRC code</param>
    /// <param name="ct">Cancellation token</param>
    Task<TrackInfo?> LookupByIsrcAsync(string isrc, CancellationToken ct);
}

/// <summary>
/// Detailed album information returned from MusicBrainz.
/// </summary>
public record AlbumDetail
{
    public Guid Mbid { get; init; }
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public IReadOnlyList<string>? ArtistNames { get; init; }
    public IReadOnlyList<string>? TrackTitles { get; init; }
    public IReadOnlyList<Guid>? TrackMbids { get; init; }
    public string? FirstReleaseDate { get; init; }
    public string? PrimaryGenre { get; init; }
    public IReadOnlyList<string>? CoverArtUrls { get; init; }
}

/// <summary>
/// Interface for ListenBrainz popularity provider.
/// Provides play count data for artist ranking and track prioritization.
/// </summary>
public interface IPopularityProvider
{
    /// <summary>
    /// Get top recordings for an artist, ranked by play count.
    /// </summary>
    /// <param name="artistMbid">MusicBrainz artist MBID</param>
    /// <param name="count">Number of top recordings to return</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<RankedRecording>> TopRecordingsForArtistAsync(Guid artistMbid, int count, CancellationToken ct);
}

/// <summary>
/// Ranked recording from ListenBrainz with play count.
/// </summary>
public record RankedRecording
{
    public Guid RecordingMbid { get; init; }
    public string Title { get; init; } = string.Empty;
    public int PlayCount { get; init; }
    public int Position { get; init; }
    public DateTime? LastPlayed { get; init; }
}

/// <summary>
/// Interface for Spotify public playlist source.
/// Uses public-page parsing and requires no Spotify credentials.
/// </summary>
public interface IPlaylistSource
{
    /// <summary>
    /// Get playlist information from a public Spotify playlist URL or ID.
    /// </summary>
    /// <param name="urlOrId">Spotify playlist URL or ID</param>
    /// <param name="ct">Cancellation token</param>
    Task<PlaylistInfo> GetPlaylistAsync(string urlOrId, CancellationToken ct);
}

/// <summary>
/// Playlist information from a public Spotify playlist.
/// </summary>
public record PlaylistInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int TotalTracks { get; init; }
    public string? OwnerDisplayName { get; init; }
    public IReadOnlyList<PlaylistTrack> Tracks { get; init; } = System.Array.Empty<PlaylistTrack>();
}

/// <summary>
/// Individual track from a Spotify playlist.
/// </summary>
public record PlaylistTrack
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ArtistName { get; init; }
    public string? AlbumName { get; init; }
    public int TrackNumber { get; init; }
    public int DurationMs { get; init; }
    public bool? IsExplicit { get; init; }
}
