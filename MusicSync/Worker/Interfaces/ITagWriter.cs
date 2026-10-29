using System.Threading;
using System.Threading.Tasks;
using MusicSync.Core;

namespace MusicSync.Worker.Interfaces;

/// <summary>
/// Writes canonical metadata and optional cover art to an acquired audio file.
/// </summary>
public interface ITagWriter
{
    Task WriteAsync(string filePath, TrackInfo track, byte[]? coverArt, CancellationToken ct);
}
