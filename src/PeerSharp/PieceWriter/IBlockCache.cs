namespace PeerSharp.PieceWriter;

internal interface IBlockCache : IDisposable
{
    /// <summary>
    /// Initialize the cache with the storage backend.
    /// </summary>
    void Initialize(IStorage storage);

    /// <summary>
    /// Reads data from the cache or fallback to storage.
    /// </summary>
    Task<bool> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Writes data to the cache (and optionally flushes to storage).
    /// </summary>
    Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
