using System.Diagnostics;
using System.Globalization;
using MusicSync.Core;

namespace MusicSync.Infrastructure;

/// <summary>
/// Wraps the ffmpeg and ffprobe executables. Every argument is passed through ArgumentList.
/// </summary>
public sealed class FfmpegAudioTranscoder
{
    private readonly ToolOptions _tools;
    private readonly AudioOptions _audio;

    public FfmpegAudioTranscoder(ToolOptions tools, AudioOptions audio)
    {
        _tools = tools;
        _audio = audio;
    }

    public async Task TranscodeToMp3Async(string inputPath, string outputPath, TrackInfo track, CancellationToken ct)
    {
        var startInfo = ExternalProcess.CreateStartInfo(_tools.Ffmpeg);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("-1");
        startInfo.ArgumentList.Add("-codec:a");
        startInfo.ArgumentList.Add("libmp3lame");
        startInfo.ArgumentList.Add("-q:a");
        startInfo.ArgumentList.Add(_audio.Quality.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-id3v2_version");
        startInfo.ArgumentList.Add("3");

        AddMetadata(startInfo, "title", track.Title);
        AddMetadata(startInfo, "artist", track.Artists.Count > 0 ? track.GetArtistNamesJoined("; ") : track.ArtistName);
        AddMetadata(startInfo, "album", track.AlbumTitle);
        AddMetadata(startInfo, "MUSICBRAINZ_TRACKID", track.RecordingMbid.ToString());

        startInfo.ArgumentList.Add(outputPath);

        var (exitCode, _, stderr) = await ExternalProcess.RunAsync(startInfo, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg falló con código {exitCode}: {ExternalProcess.LastLine(stderr)}");
    }

    public async Task<int?> ProbeDurationMsAsync(string inputPath, CancellationToken ct)
    {
        var startInfo = ExternalProcess.CreateStartInfo(_tools.Ffprobe);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputPath);

        var (exitCode, stdout, _) = await ExternalProcess.RunAsync(startInfo, ct);
        if (exitCode != 0)
            return null;

        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? (int)Math.Round(seconds * 1000)
            : null;
    }

    private static void AddMetadata(ProcessStartInfo startInfo, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        startInfo.ArgumentList.Add("-metadata");
        startInfo.ArgumentList.Add($"{key}={value}");
    }
}
