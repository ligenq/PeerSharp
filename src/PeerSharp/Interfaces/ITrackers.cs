namespace PeerSharp.Interfaces;

/// <summary>
/// Provides methods for monitoring and managing trackers for a torrent.
/// </summary>
public interface ITrackers
{
    /// <summary>
    /// Adds a new tracker URL to the torrent.
    /// </summary>
    /// <param name="url">The tracker announce URL.</param>
    void AddTracker(string url);

    /// <summary>
    /// Manually triggers an announce to all trackers or a specific one.
    /// </summary>
    /// <param name="url">Optional URL of the specific tracker to announce to. If null, announces to all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the announce requests have been initiated.</returns>
    Task AnnounceAsync(string? url = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a snapshot of the current status for all trackers.
    /// </summary>
    /// <returns>A read-only list of tracker statuses.</returns>
    IReadOnlyList<TrackerStatus> GetTrackers();

    /// <summary>
    /// Removes a tracker URL from the torrent.
    /// </summary>
    /// <param name="url">The tracker URL to remove.</param>
    /// <returns>True if the tracker was found and removed, false otherwise.</returns>
    bool RemoveTracker(string url);
}
