using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExternalRatings.Persistence;

/// <summary>
/// A single-file byte store with atomic replacement. The seam that keeps the stores off real disk in
/// unit tests (spec §12.1). One instance is bound to one file.
/// </summary>
internal interface ICacheFileStore
{
    /// <summary>Reads the file contents.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bytes, or <see langword="null"/> if the file does not exist.</returns>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the file contents atomically (temp file then replace).</summary>
    /// <param name="content">The bytes to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the write is durable.</returns>
    Task WriteAtomicAsync(byte[] content, CancellationToken cancellationToken);

    /// <summary>Deletes the file if it exists.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once deletion is durable.</returns>
    Task DeleteAsync(CancellationToken cancellationToken);
}
