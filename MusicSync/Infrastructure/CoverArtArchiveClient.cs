using System.Net;
using Microsoft.Extensions.Logging;
using MusicSync.Core;
using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure;

/// <summary>
/// Fetches front cover art from the Cover Art Archive. Redirects are followed manually so
/// every hop stays on coverartarchive.org or archive.org (§12 outbound hardening). Cover art is
/// optional: failures return null instead of failing the job.
/// </summary>
public sealed class CoverArtArchiveClient : ICoverArtProvider, IDisposable
{
    private const int MaxRedirects = 4;

    private static readonly string[] AllowedHosts = { "coverartarchive.org", "archive.org" };

    private readonly CoverArtOptions _options;
    private readonly ILogger<CoverArtArchiveClient>? _logger;
    private readonly HttpClient _httpClient;

    public CoverArtArchiveClient(
        CoverArtOptions options,
        string userAgent,
        ILogger<CoverArtArchiveClient>? logger = null,
        HttpMessageHandler? handler = null)
    {
        _options = options;
        _logger = logger;

        var httpHandler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(httpHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<byte[]?> GetFrontCoverAsync(Guid releaseGroupMbid, CancellationToken ct)
    {
        if (!_options.Enabled || releaseGroupMbid == Guid.Empty)
            return null;

        var uri = new Uri($"https://coverartarchive.org/release-group/{releaseGroupMbid:D}/front-500");

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsAllowed(uri))
                {
                    _logger?.LogWarning("Cover art redirect left the allowlist at {Host}.", uri.Host);
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return null;

                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning(
                        "Cover Art Archive returned {StatusCode} for release group {ReleaseGroupMbid}.",
                        (int)response.StatusCode, releaseGroupMbid);
                    return null;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("Cover Art Archive returned non-image content for {ReleaseGroupMbid}.", releaseGroupMbid);
                    return null;
                }

                if (response.Content.Headers.ContentLength is long length && length > _options.MaxBytes)
                {
                    _logger?.LogWarning("Cover art for {ReleaseGroupMbid} is larger than {MaxBytes} bytes.", releaseGroupMbid, _options.MaxBytes);
                    return null;
                }

                return await ReadCappedAsync(response.Content, ct);
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("Cover art request timed out for release group {ReleaseGroupMbid}.", releaseGroupMbid);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Cover art request failed for release group {ReleaseGroupMbid}.", releaseGroupMbid);
            return null;
        }
    }

    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                return buffer.ToArray();

            total += read;
            if (total > _options.MaxBytes)
            {
                _logger?.LogWarning("Cover art exceeded {MaxBytes} bytes while downloading.", _options.MaxBytes);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return AllowedHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _httpClient.Dispose();
}
