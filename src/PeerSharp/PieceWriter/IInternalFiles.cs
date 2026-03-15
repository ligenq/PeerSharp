namespace PeerSharp.PieceWriter;

/// <summary>
/// Internal interface for file operations.
/// </summary>
internal interface IInternalFiles : IFiles
{
    /// <summary>
    /// Gets or sets whether files are currently being checked.
    /// Internal-only setter.
    /// </summary>
    new bool Checking { get; set; }

    Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct);

    Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);

    Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
}
