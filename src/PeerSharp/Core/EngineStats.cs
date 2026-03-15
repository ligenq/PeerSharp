namespace PeerSharp.Core;

/// <summary>
/// Provides aggregate statistics for the entire BitTorrent client engine.
/// </summary>
/// <param name="DownloadSpeed">The current aggregate download speed in bytes per second.</param>
/// <param name="UploadSpeed">The current aggregate upload speed in bytes per second.</param>
/// <param name="TotalDownloaded">The total bytes downloaded in the current session.</param>
/// <param name="TotalUploaded">The total bytes uploaded in the current session.</param>
/// <param name="TorrentCount">The number of torrents currently being managed.</param>
/// <param name="ActiveTorrents">The number of torrents that are currently active.</param>
/// <param name="TotalPeers">The total number of connected peers across all torrents.</param>
public sealed record EngineStats(
    int DownloadSpeed = 0,
    int UploadSpeed = 0,
    long TotalDownloaded = 0,
    long TotalUploaded = 0,
    int TorrentCount = 0,
    int ActiveTorrents = 0,
    int TotalPeers = 0);

