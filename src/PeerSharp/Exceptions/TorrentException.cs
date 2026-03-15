namespace PeerSharp.Exceptions;

/// <summary>
/// Exception thrown when a torrent already exists in the client.
/// </summary>
public class TorrentAlreadyExistsException : TorrentException
{
    /// <summary>
    /// Creates a new TorrentAlreadyExistsException.
    /// </summary>
    /// <param name="existingTorrent">The existing torrent.</param>
    public TorrentAlreadyExistsException(ITorrent existingTorrent)
        : base($"Torrent '{existingTorrent.Name}' already exists.", existingTorrent.Hash)
    {
        ExistingTorrent = existingTorrent;
    }

    /// <summary>
    /// Gets the existing torrent.
    /// </summary>
    public ITorrent ExistingTorrent { get; }
}

/// <summary>
/// Exception thrown when a torrent operation fails.
/// </summary>
public class TorrentException : Exception
{
    /// <summary>
    /// Creates a new TorrentException with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TorrentException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a new TorrentException with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TorrentException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a new TorrentException with a message and info hash.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="infoHash">The info hash of the torrent.</param>
    public TorrentException(string message, InfoHash infoHash) : base(message)
    {
        InfoHash = infoHash;
    }

    /// <summary>
    /// Creates a new TorrentException with a message, info hash, and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="infoHash">The info hash of the torrent.</param>
    /// <param name="innerException">The inner exception.</param>
    public TorrentException(string message, InfoHash infoHash, Exception innerException) : base(message, innerException)
    {
        InfoHash = infoHash;
    }

    /// <summary>
    /// Gets the info hash of the torrent that caused the exception, if available.
    /// </summary>
    public InfoHash? InfoHash { get; }
}

/// <summary>
/// Exception thrown when a torrent is not found.
/// </summary>
public class TorrentNotFoundException : TorrentException
{
    /// <summary>
    /// Creates a new TorrentNotFoundException.
    /// </summary>
    /// <param name="infoHash">The info hash that was not found.</param>
    public TorrentNotFoundException(InfoHash infoHash)
        : base($"Torrent with hash '{infoHash}' not found.", infoHash)
    {
    }
}
