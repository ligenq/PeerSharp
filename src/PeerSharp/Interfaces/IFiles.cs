namespace PeerSharp.Interfaces;

/// <summary>
/// Provides access to file information and operations for a torrent.
/// </summary>
public interface IFiles
{
    /// <summary>
    /// Gets whether files are currently being checked/verified.
    /// </summary>
    bool Checking { get; }

    /// <summary>
    /// Gets the download path for this torrent's files.
    /// </summary>
    string DownloadPath { get; }
}
