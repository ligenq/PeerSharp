using Microsoft.Win32.SafeHandles;

namespace PeerSharp.PieceWriter;

/// <summary>
/// A borrowed file handle. The handle stays open for as long as the lease is held, so callers
/// must dispose it as soon as the read or write completes - holding one indefinitely keeps a
/// descriptor out of the shared cache.
/// </summary>
public interface IFileHandleLease : IDisposable
{
    /// <summary>Gets the open handle. Valid only until the lease is disposed.</summary>
    SafeFileHandle Handle { get; }

    /// <summary>Gets the full path of the file the handle refers to.</summary>
    string Path { get; }
}

/// <summary>
/// A global cache for open file handles, inspired by libtransmission's tr_open_files.
/// Limits the number of simultaneously open file descriptors across all torrents.
/// </summary>
internal interface IFileHandleCache : IDisposable
{
    /// <summary>
    /// Closes and removes all handles associated with a specific directory (used when removing a torrent).
    /// </summary>
    void CloseTorrentHandles(string rootPath);

    /// <summary>
    /// Acquires a file handle lease for the specified path.
    /// The lease guarantees the handle remains open until the lease is disposed.
    /// </summary>
    /// <param name="path">The full path to the file.</param>
    /// <param name="writable">Whether the file needs to be opened for writing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease containing the SafeFileHandle.</returns>
    ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default);
}
