using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// A file-backed <see cref="ICacheFileStore"/> bound to a single path. Writes go to a sibling temp
/// file that then atomically replaces the target, so a crash mid-write never corrupts the store
/// (spec §7.1). The production seam behind <c>FileRatingCache</c>/<c>BackupStore</c>.
/// </summary>
internal sealed class FileCacheFileStore : ICacheFileStore
{
    private readonly string _filePath;

    /// <summary>Initializes a new instance of the <see cref="FileCacheFileStore"/> class.</summary>
    /// <param name="filePath">The absolute path of the backing file.</param>
    public FileCacheFileStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteAtomicAsync(byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <inheritdoc />
    public async Task AppendAsync(byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }

        return Task.CompletedTask;
    }
}
