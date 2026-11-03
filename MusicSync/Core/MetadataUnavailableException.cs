namespace MusicSync.Core;

/// <summary>
/// Raised when an external metadata provider cannot answer after retries.
/// </summary>
public sealed class MetadataUnavailableException : Exception
{
    public MetadataUnavailableException(string operation, int? statusCode, Exception? inner = null)
        : base(statusCode is null
            ? $"Metadata provider unavailable during {operation}."
            : $"Metadata provider returned {statusCode} during {operation}.", inner)
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }

    public int? StatusCode { get; }
}
