using Jellyfin.Plugin.ExternalRatings.Persistence;

namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ICacheFileStore"/> so store unit tests never touch real disk.
/// </summary>
internal sealed class InMemoryCacheFileStore : ICacheFileStore
{
    private byte[]? _content;

    public InMemoryCacheFileStore()
    {
    }

    public InMemoryCacheFileStore(byte[]? initial) => _content = initial;

    public int WriteCount { get; private set; }

    public bool Exists => _content is not null;

    public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(_content?.ToArray());

    public Task WriteAtomicAsync(byte[] content, CancellationToken cancellationToken)
    {
        _content = content.ToArray();
        WriteCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        _content = null;
        return Task.CompletedTask;
    }

    /// <summary>Seeds raw bytes directly (used to simulate corrupt or foreign content).</summary>
    /// <param name="content">The raw bytes.</param>
    public void SetRawContent(byte[]? content) => _content = content is null ? null : content.ToArray();
}
