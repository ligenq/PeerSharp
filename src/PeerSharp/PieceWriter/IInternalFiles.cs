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

    /// <summary>
    /// Forces written piece data out to the physical device. Returns <see langword="false"/> if any
    /// file could not be flushed; see <see cref="IStorage.FlushAsync"/> for why the caller cares.
    /// </summary>
    Task<bool> FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves this torrent's files under a new root. See <see cref="IStorage.MoveAsync"/>.
    /// </summary>
    Task MoveFilesAsync(string newRootPath, CancellationToken ct = default);

    /// <summary>
    /// Renames one file in place. See <see cref="IStorage.RenameFileAsync"/>.
    /// </summary>
    Task RenameFileAsync(int fileIndex, string newRelativePath, CancellationToken ct = default);

    Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct);

    Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);

    Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
}
