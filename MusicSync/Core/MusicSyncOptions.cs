namespace MusicSync.Core;

public sealed class LibraryOptions
{
    public string Root { get; set; } = string.Empty;

    public string StagingDirectory { get; set; } = string.Empty;
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

public sealed class AcquisitionOptions
{
    public string LocalSourceDirectory { get; set; } = string.Empty;
}
