using Microsoft.Win32.SafeHandles;

namespace PeerSharp.PieceWriter;

public interface IFileHandleLease : IDisposable
{
    SafeFileHandle Handle { get; }
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
    /// <returns>A lease containing the SafeFileHandle.</returns>
    ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default);
}
