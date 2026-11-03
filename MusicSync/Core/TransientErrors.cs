namespace MusicSync.Core;

/// <summary>
/// Failure reported by an acquisition provider for a condition that may pass on its own
/// (rate limiting, network trouble, provider outage).
/// </summary>
public sealed class TransientAcquisitionException : Exception
{
    public TransientAcquisitionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public static class TransientErrors
{
    public static bool IsTransient(Exception exception) =>
        exception is TransientAcquisitionException
        || exception is MetadataUnavailableException
        || exception is System.Net.Http.HttpRequestException
        || exception is TimeoutException;
}
