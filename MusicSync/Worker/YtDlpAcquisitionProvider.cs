using MusicSync.Core;
using MusicSync.Infrastructure;

namespace MusicSync.Worker;

/// <summary>
/// yt-dlp acquisition provider. Searches for candidates and downloads the selected video's audio
/// into a temporary work directory. Every argument is passed through ArgumentList; the video URL
/// is validated and placed after "--" so it cannot be read as an option.
/// </summary>
public sealed class YtDlpAcquisitionProvider : IAcquisitionProvider
{
    private readonly ToolOptions _tools;
    private readonly string _workRoot;

    public YtDlpAcquisitionProvider(ToolOptions tools, string workRoot)
    {
        _tools = tools;
        _workRoot = Path.GetFullPath(workRoot);
    }

    public string Name => "yt-dlp";

    public async Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || max <= 0)
            return Array.Empty<SourceCandidate>();

        var query = string.IsNullOrWhiteSpace(track.ArtistName)
            ? track.Title
            : $"{track.Title} - {track.ArtistName}";

        var startInfo = ExternalProcess.CreateStartInfo(_tools.YtDlp);
        startInfo.ArgumentList.Add("--flat-playlist");
        startInfo.ArgumentList.Add("--dump-json");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add($"ytsearch{max}:{query}");

        var (exitCode, stdout, stderr) = await ExternalProcess.RunAsync(startInfo, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"yt-dlp falló al buscar: {ExternalProcess.LastLine(stderr)}");

        return YtDlpSearchParser.Parse(stdout, max)
            .Select(video => new SourceCandidate
            {
                Title = video.Title,
                SourceUrl = YtDlpSearchParser.ToWatchUrl(video.VideoId),
                DurationMs = video.DurationMs
            })
            .ToList();
    }

    public async Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
    {
        if (!YtDlpSearchParser.IsWatchUrl(candidate.SourceUrl))
            throw new InvalidOperationException("La URL de origen no es un video de YouTube válido.");

        var directory = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var startInfo = ExternalProcess.CreateStartInfo(_tools.YtDlp);
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--no-part");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("bestaudio");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(Path.Combine(directory, "source.%(ext)s"));
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(candidate.SourceUrl);

            var (exitCode, _, stderr) = await ExternalProcess.RunAsync(startInfo, ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"yt-dlp falló al descargar: {ExternalProcess.LastLine(stderr)}");

            var file = Directory.EnumerateFiles(directory).FirstOrDefault()
                ?? throw new InvalidOperationException("yt-dlp no produjo ningún archivo de audio.");

            return new DownloadedFile { FilePath = file, IsTemporary = true };
        }
        catch
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            throw;
        }
    }
}
