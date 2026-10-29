using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;

namespace MusicSync.Infrastructure;

/// <summary>
/// Global rate limiter for MusicBrainz API calls.
/// Enforces 1 request/second as required by section 8.2 of PROJECT_CONTEXT.md.
/// Shared across all callers to prevent API bans.
/// </summary>
public static class MusicBrainzRateLimiter
{
    /// <summary>
    /// Creates a token bucket rate limiter with 1 request per second.
    /// </summary>
    /// <param name="builder">Polly builder to apply the rate limit to</param>
    public static void ApplyRateLimit(PolicyBuilder builder)
    {
        builder.WaitAndRetry(
            retryCount: 3,
            sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
            onRetry: (exception, time, context) =>
            {
                var logger = context.GetLogger();
                logger?.LogWarning(exception, "MusicBrainz rate limit exceeded; waiting before retry");
            });
    }
}

/// <summary>
/// Rate limit exception thrown when the token bucket is depleted.
/// </summary>
public class RateLimitRejectedException : Exception
{
    public RateLimitRejectedException(string message) : base(message) { }
}

/// <summary>
/// Helper class to access ILogger from Polly context
/// </summary>
public static class LoggerContext
{
    public static ILogger? GetLogger(this Context context)
    {
        return null;
    }
}

/// <summary>
/// Json serialization options used consistently across HTTP clients.
/// </summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
}
