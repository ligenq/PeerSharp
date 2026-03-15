namespace PeerSharp.Config;

/// <summary>
/// Options for removing a torrent from the client.
/// </summary>
[Flags]
public enum RemoveOptions
{
    /// <summary>
    /// No additional actions - keep all files (default).
    /// </summary>
    None = 0,

    /// <summary>
    /// Delete downloaded files when removing the torrent.
    /// </summary>
    DeleteFiles = 1,

    /// <summary>
    /// Delete the .torrent file when removing the torrent.
    /// </summary>
    DeleteTorrentFile = 2,

    /// <summary>
    /// Delete both downloaded files and the .torrent file.
    /// </summary>
    DeleteAll = DeleteFiles | DeleteTorrentFile
}

