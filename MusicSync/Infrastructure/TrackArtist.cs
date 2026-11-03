namespace MusicSync.Infrastructure;

public class TrackArtist
{
    public int TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public int ArtistId { get; set; }

    public Artist Artist { get; set; } = null!;

    public int Position { get; set; }
}
