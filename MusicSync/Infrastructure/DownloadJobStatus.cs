namespace MusicSync.Infrastructure;

public static class DownloadJobStatus
{
    public const string Queued = "Queued";
    public const string Resolving = "Resolving";
    public const string Searching = "Searching";
    public const string NeedsReview = "NeedsReview";
    public const string Approved = "Approved";
    public const string Downloading = "Downloading";
    public const string Tagging = "Tagging";
    public const string ReadyForTagging = "ReadyForTagging";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Queued,
        Resolving,
        Searching,
        NeedsReview,
        Approved,
        Downloading,
        Tagging,
        ReadyForTagging,
        Done,
        Failed,
        Cancelled
    };

    public static IReadOnlySet<string> Cancellable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Queued,
        Resolving,
        Searching,
        NeedsReview,
        Approved,
        Downloading,
        Tagging,
        ReadyForTagging
    };

    /// <summary>Jobs that already own the track and would duplicate work for a new enqueue.</summary>
    public static IReadOnlySet<string> Active { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Queued,
        Resolving,
        Searching,
        NeedsReview,
        Approved,
        Downloading,
        Tagging,
        ReadyForTagging
    };
}
