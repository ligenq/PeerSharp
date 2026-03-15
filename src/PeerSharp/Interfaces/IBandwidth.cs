namespace PeerSharp.Interfaces;

/// <summary>
/// Provides methods for managing bandwidth limits.
/// </summary>
public interface IBandwidth
{
    /// <summary>
    /// Gets the current bandwidth limits for a specific torrent.
    /// </summary>
    /// <param name="torrent">The torrent.</param>
    /// <returns>A tuple containing (downloadLimit, uploadLimit).</returns>
    (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent);

    /// <summary>
    /// Gets the current disk bandwidth limits for a specific torrent.
    /// </summary>
    /// <param name="torrent">The torrent.</param>
    /// <returns>A tuple containing (readLimit, writeLimit).</returns>
    (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent);

    /// <summary>
    /// Sets global download and upload speed limits.
    /// </summary>
    /// <param name="downloadLimit">Maximum download speed in bytes per second. 0 for unlimited.</param>
    /// <param name="uploadLimit">Maximum upload speed in bytes per second. 0 for unlimited.</param>
    void SetGlobalLimits(int downloadLimit, int uploadLimit);

    /// <summary>
    /// Sets global disk read and write speed limits.
    /// </summary>
    /// <param name="readLimit">Maximum disk read speed in bytes per second. 0 for unlimited.</param>
    /// <param name="writeLimit">Maximum disk write speed in bytes per second. 0 for unlimited.</param>
    void SetGlobalDiskLimits(int readLimit, int writeLimit);

    /// <summary>
    /// Sets bandwidth limits for a specific torrent.
    /// </summary>
    /// <param name="torrent">The torrent to set limits for.</param>
    /// <param name="downloadLimit">Maximum download speed in bytes per second. 0 for unlimited.</param>
    /// <param name="uploadLimit">Maximum upload speed in bytes per second. 0 for unlimited.</param>
    void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit);

    /// <summary>
    /// Sets disk read and write limits for a specific torrent.
    /// </summary>
    /// <param name="torrent">The torrent to set limits for.</param>
    /// <param name="readLimit">Maximum disk read speed in bytes per second. 0 for unlimited.</param>
    /// <param name="writeLimit">Maximum disk write speed in bytes per second. 0 for unlimited.</param>
    void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit);
}
