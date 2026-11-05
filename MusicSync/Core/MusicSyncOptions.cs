namespace MusicSync.Core;

public sealed class LibraryOptions
{
    public string Root { get; set; } = string.Empty;

    public string StagingDirectory { get; set; } = string.Empty;

    /// <summary>Search YouTube when MusicBrainz has no candidate matching the file's artist.</summary>
    public bool YouTubeFallback { get; set; } = true;

    /// <summary>How many YouTube results to compare per file.</summary>
    public int YouTubeMaxCandidates { get; set; } = 5;

    /// <summary>Embed a Cover Art Archive front cover into files accepted during library import.</summary>
    public bool EmbedCoverOnImport { get; set; } = true;
}

public sealed class AudioOptions
{
    public string Format { get; set; } = "mp3";

    public int Quality { get; set; } = 0;
}

public sealed class ToolOptions
{
    public string Ffmpeg { get; set; } = "ffmpeg";

    public string Ffprobe { get; set; } = "ffprobe";

    public string YtDlp { get; set; } = "yt-dlp";
}

public sealed class CoverArtOptions
{
    /// <summary>Fetch cover art from the Cover Art Archive when tagging a downloaded track.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Largest cover file accepted, in bytes.</summary>
    public int MaxBytes { get; set; } = 5_000_000;

    /// <summary>Cover Art Archive request timeout, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class AcquisitionOptions
{
    public string LocalSourceDirectory { get; set; } = string.Empty;
}

public sealed class MusicBrainzOptions
{
    /// <summary>
    /// Identifying User-Agent required by MusicBrainz (section 8.2). Format: "Name/Version (contact)".
    /// Also used for ListenBrainz requests. MusicBrainz needs no account or API key.
    /// </summary>
    public string UserAgent { get; set; } = "MusicSync/0.1 (https://github.com/ismabellmann/musicsearcher)";
}

public sealed class MatchingOptions
{
    public double AutoAcceptThreshold { get; set; } = 0.80;

    public double ReviewThreshold { get; set; } = 0.50;

    public int DurationToleranceSeconds { get; set; } = 5;
}

public sealed class WorkerOptions
{
    public int Concurrency { get; set; } = 1;

    /// <summary>Automatic retries for transient failures before a job is marked Failed.</summary>
    public int MaxAutomaticRetries { get; set; } = 2;

    /// <summary>Base backoff in seconds; retry n waits base x 2^(n-1), capped at 10 minutes.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;
}

public sealed class AuthOptions
{
    /// <summary>When true the session cookie is always sent with the Secure flag. Leave false for plain HTTP.</summary>
    public bool SecureCookies { get; set; } = false;

    /// <summary>Session lifetime in hours (1 to 720).</summary>
    public int SessionHours { get; set; } = 168;
}
