using System.Diagnostics;

namespace MusicSync.Infrastructure;

/// <summary>
/// Runs external tools. Callers add arguments through ArgumentList; no command line is built from strings.
/// </summary>
public static class ExternalProcess
{
    public static ProcessStartInfo CreateStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken ct)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"No se pudo iniciar {startInfo.FileName}.");

        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        ct.ThrowIfCancellationRequested();

        return (process.ExitCode, stdout, stderr);
    }

    public static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }
}
