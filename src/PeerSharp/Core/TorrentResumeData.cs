namespace PeerSharp.Core;

/// <summary>
/// Contains all information necessary to resume a torrent session without a full recheck.
/// This includes verified pieces bitfield, unfinished blocks, and download statistics.
/// </summary>
public sealed class TorrentResumeData
{
    /// <summary>
    /// Gets the raw resume data bytes. These can be saved by the application and
    /// passed back to AddTorrentAsync to resume the session.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Gets the info hash of the torrent this resume data belongs to.
    /// </summary>
    public InfoHash Hash { get; init; }

    /// <summary>
    /// Gets the timestamp when this resume data was captured.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

