namespace PeerSharp.PieceWriter;

internal interface IStorage : IAsyncDisposable
{
    Task DeleteAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Forces everything written since the last flush out to the physical device. Returns
    /// <see langword="false"/> if any file could not be flushed, in which case the caller must not
    /// record those bytes as durably stored.
    /// </summary>
    Task<bool> FlushAsync(CancellationToken ct = default);

    Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default);

    ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);

    Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default);

    Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
