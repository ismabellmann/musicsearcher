using MusicSync.Core.Interfaces;

namespace MusicSync.Infrastructure.Tests;

/// <summary>Cover-art provider that never returns an image, for tests that do not exercise covers.</summary>
internal sealed class NoCoverArtProvider : ICoverArtProvider
{
    public Task<byte[]?> GetFrontCoverAsync(Guid releaseGroupMbid, CancellationToken ct)
        => Task.FromResult<byte[]?>(null);
}
