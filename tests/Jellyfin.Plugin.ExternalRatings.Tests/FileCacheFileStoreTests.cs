using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ExternalRatings.Persistence;
using Xunit;

namespace Jellyfin.Plugin.ExternalRatings.Tests;

public sealed class FileCacheFileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public FileCacheFileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "er-filestore-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "nested", "store.json");
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        var store = new FileCacheFileStore(_file);

        (await store.ReadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var store = new FileCacheFileStore(_file);

        await store.WriteAtomicAsync(Bytes("hello"), CancellationToken.None);
        var read = await store.ReadAsync(CancellationToken.None);

        read.Should().NotBeNull();
        Encoding.UTF8.GetString(read!).Should().Be("hello");
    }

    [Fact]
    public async Task WriteAtomic_CreatesParentDirectory()
    {
        var store = new FileCacheFileStore(_file);

        await store.WriteAtomicAsync(Bytes("x"), CancellationToken.None);

        File.Exists(_file).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAtomic_Overwrites()
    {
        var store = new FileCacheFileStore(_file);

        await store.WriteAtomicAsync(Bytes("first"), CancellationToken.None);
        await store.WriteAtomicAsync(Bytes("second"), CancellationToken.None);

        Encoding.UTF8.GetString((await store.ReadAsync(CancellationToken.None))!).Should().Be("second");
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var store = new FileCacheFileStore(_file);
        await store.WriteAtomicAsync(Bytes("x"), CancellationToken.None);

        await store.DeleteAsync(CancellationToken.None);

        (await store.ReadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_MissingFile_DoesNotThrow()
    {
        var store = new FileCacheFileStore(_file);

        var act = async () => await store.DeleteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
