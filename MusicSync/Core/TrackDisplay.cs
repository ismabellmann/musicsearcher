namespace MusicSync.Core;

public static class TrackDisplay
{
    /// <summary>
    /// "Title - Artist &amp; Second Artist", the form used when comparing candidates to a track.
    /// </summary>
    public static string Title(TrackInfo track)
    {
        var artists = track.Artists.Count > 0 ? track.GetArtistNamesJoined(" & ") : track.ArtistName;
        return string.IsNullOrWhiteSpace(artists) ? track.Title : $"{track.Title} - {artists}";
    }
}
