namespace PeerSharp.Interfaces;

/// <summary>
/// Provides access to metadata download progress for magnet link torrents.
/// </summary>
public interface IMetadataDownload
{
    /// <summary>
    /// Gets whether metadata download has finished.
    /// </summary>
    bool Finished { get; }

    /// <summary>
    /// Gets the progress of metadata download (0.0 to 1.0).
    /// </summary>
    float Progress { get; }

    /// <summary>
    /// Starts the metadata download process.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the metadata download process.
    /// </summary>
    void Stop();
}
