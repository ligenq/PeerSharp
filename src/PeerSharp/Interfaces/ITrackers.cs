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
    /// <param name="cancellationToken">
    /// Cancels the scheduling of the announces. It does not cancel announces already in flight -
    /// each runs under its own tracker timeout and is aborted when the torrent stops.
    /// </param>
    /// <returns>A task that completes when the announce requests have been initiated.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the token is already cancelled.</exception>
    Task AnnounceAsync(string? url = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the trackers how many seeders and leechers they know of, without announcing this client.
    /// </summary>
    /// <param name="url">A specific tracker to ask. If null, every active tracker is asked.</param>
    /// <param name="cancellationToken">Cancels the scrape.</param>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="AnnounceAsync(string, CancellationToken)"/> this waits for the replies, since
    /// the counts are the only reason to call it. Read them from
    /// <see cref="TrackerStatus.SeedCount"/> and <see cref="TrackerStatus.LeechCount"/> on
    /// <see cref="GetTrackers"/> afterwards.
    /// </para>
    /// <para>
    /// A tracker that refuses or does not implement scrape is recorded in its own status and does not
    /// fail the call; only cancellation does.
    /// </para>
    /// </remarks>
    Task ScrapeAsync(string? url = null, CancellationToken cancellationToken = default);

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
