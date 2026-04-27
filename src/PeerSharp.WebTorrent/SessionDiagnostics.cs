namespace PeerSharp.WebTorrent;

/// <summary>
/// Snapshot of overall WebTorrent session health and resource usage.
/// </summary>
public sealed record SessionDiagnostics(
    int TrackerCount,
    int ConnectedTrackers,
    int ReconnectingTrackers,
    int PendingPeerCount,
    int EarlyCandidateOfferCount,
    bool TorrentFinished);
