using System.Diagnostics;
using MusicSync.Core;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class FfmpegAudioTranscoderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "musicsync-ffmpeg-" + Guid.NewGuid().ToString("N"));

    public FfmpegAudioTranscoderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task TranscodeToMp3_WritesMp3WithDurationAndMetadata()
    {
        var input = Path.Combine(_directory, "tone.wav");
        var output = Path.Combine(_directory, "tone.mp3");
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2", input);

        var transcoder = new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions());
        var track = new TrackInfo
        {
            RecordingMbid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "Tone Test",
            ArtistName = "Test Artist",
            DurationMs = 2000
        };

        await transcoder.TranscodeToMp3Async(input, output, track, CancellationToken.None);

        Assert.True(File.Exists(output));
        var duration = await transcoder.ProbeDurationMsAsync(output, CancellationToken.None);
        Assert.NotNull(duration);
        Assert.InRange(duration!.Value, 1900, 2200);

        var title = await ProbeFormatTagAsync(output, "title");
        Assert.Equal("Tone Test", title);
    }

    [Fact]
    public async Task ProbeDuration_MissingFile_ReturnsNull()
    {
        var transcoder = new FfmpegAudioTranscoder(new ToolOptions(), new AudioOptions());

        var duration = await transcoder.ProbeDurationMsAsync(Path.Combine(_directory, "missing.mp3"), CancellationToken.None);

        Assert.Null(duration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static async Task<string> ProbeFormatTagAsync(string path, string key)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add($"format_tags={key}");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }

    private static async Task RunToolAsync(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} failed: {stderr}");
    }
}
