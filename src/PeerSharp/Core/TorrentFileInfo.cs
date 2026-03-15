namespace PeerSharp.Core;

/// <summary>
/// Provides information about a file within a torrent.
/// </summary>
/// <param name="Path">The file path relative to the torrent root.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Index">The zero-based index of this file in the torrent.</param>
/// <param name="DownloadedBytes">The number of bytes downloaded and verified for this file.</param>
public sealed record TorrentFileInfo(
    string Path,
    long Size,
    int Index,
    long DownloadedBytes = 0)
{
    /// <summary>
    /// Gets the download progress of this file (0.0 to 1.0).
    /// </summary>
    public float Progress => Size > 0 ? (float)DownloadedBytes / Size : 1.0f;
}

