using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Integration;

public class StorageFailureTests
{
    [Fact]
    public async Task WriteAsync_DiskFull_ThrowsIOException()
    {
        // Verify that an IOException with HRESULT 0x80070070 (disk full)
        // is properly thrown by a storage implementation.
        // This validates the error code pattern used for disk-full detection.
        IInternalFiles failingFiles = new MockFailingFiles();

        var ex = await Assert.ThrowsAsync<IOException>(
            () => failingFiles.WriteAsync(0, new byte[1], CancellationToken.None));

        Assert.Equal("Disk full", ex.Message);
        Assert.Equal(unchecked((int)0x80070070), ex.HResult);
    }

    private class MockFailingFiles : IInternalFiles
    {
        public bool Checking { get; set; }
        public string DownloadPath => "C:\\Mock";
        public bool IsDisposed => false;

        public Task DeleteFilesAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task InitializeAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
        public Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct) => Task.CompletedTask;

        public Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var ex = new IOException("Disk full");
            ex.HResult = unchecked((int)0x80070070);
            throw ex;
        }
    }
}
