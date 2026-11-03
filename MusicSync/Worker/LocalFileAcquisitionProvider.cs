using MusicSync.Core;
using MusicSync.Infrastructure;

namespace MusicSync.Worker;

/// <summary>
/// Acquisition provider for audio files the user has placed in a local source folder.
/// Candidates are the audio files directly inside that folder; the source is never modified.
/// </summary>
public sealed class LocalFileAcquisitionProvider : IAcquisitionProvider
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav"
    };

    private readonly string _sourceDirectory;
    private readonly FfmpegAudioTranscoder _transcoder;

    public LocalFileAcquisitionProvider(string sourceDirectory, FfmpegAudioTranscoder transcoder)
    {
        _sourceDirectory = Path.GetFullPath(sourceDirectory);
        _transcoder = transcoder;
    }

    public string Name => "local";

    public async Task<IReadOnlyList<SourceCandidate>> FindCandidatesAsync(TrackInfo track, int max, CancellationToken ct)
    {
        if (!Directory.Exists(_sourceDirectory) || max <= 0)
            return Array.Empty<SourceCandidate>();

        var files = Directory.EnumerateFiles(_sourceDirectory)
            .Where(file => AudioExtensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Take(max)
            .ToList();

        var candidates = new List<SourceCandidate>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            candidates.Add(new SourceCandidate
            {
                Title = Path.GetFileNameWithoutExtension(file),
                SourceUrl = Path.GetFileName(file),
                Format = Path.GetExtension(file).ToLowerInvariant(),
                DurationMs = await _transcoder.ProbeDurationMsAsync(file, ct)
            });
        }

        return candidates;
    }

    public Task<DownloadedFile> DownloadAsync(SourceCandidate candidate, CancellationToken ct)
    {
        return Task.FromResult(new DownloadedFile { FilePath = ResolveSourcePath(candidate.SourceUrl) });
    }

    private string ResolveSourcePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            throw new InvalidOperationException("El nombre del archivo de origen no es válido.");

        var fullPath = Path.GetFullPath(Path.Combine(_sourceDirectory, fileName));
        if (!string.Equals(Path.GetDirectoryName(fullPath), _sourceDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("El archivo de origen queda fuera de la carpeta configurada.");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("El archivo de origen no existe.", fullPath);

        return fullPath;
    }
}
