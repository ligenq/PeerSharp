namespace PeerSharp.PieceWriter;

internal interface IStorage : IAsyncDisposable
{
    Task DeleteAllAsync(CancellationToken ct = default);

    Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default);

    ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);

    Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default);

    Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
