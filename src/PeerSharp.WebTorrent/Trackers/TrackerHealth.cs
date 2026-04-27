namespace PeerSharp.WebTorrent.Trackers;

/// <summary>
/// Snapshot of a WebTorrent tracker's current health.
/// </summary>
public sealed record TrackerHealth(string Url, bool IsConnected, int ConsecutiveFailures, string? LastError, DateTimeOffset NextReconnectAt);
