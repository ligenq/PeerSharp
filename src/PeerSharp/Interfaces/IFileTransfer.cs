namespace PeerSharp.Interfaces;

/// <summary>
/// Provides access to file transfer statistics and piece management for a torrent.
/// </summary>
public interface IFileTransfer
{
    /// <summary>
    /// Gets the total bytes downloaded.
    /// </summary>
    long Downloaded { get; }

    /// <summary>
    /// Gets whether end-game mode is active (requesting remaining pieces from all peers).
    /// </summary>
    bool EndGameMode { get; }

    /// <summary>
    /// Gets the total bytes uploaded.
    /// </summary>
    long Uploaded { get; }
}
