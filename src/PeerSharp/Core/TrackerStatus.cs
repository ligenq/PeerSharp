namespace PeerSharp.Core;

/// <summary>
/// Specifies the operational status of a tracker.
/// </summary>
public enum TrackerStatusType
{
    /// <summary>Tracker is responding normally.</summary>
    Working,

    /// <summary>Last announce attempt failed.</summary>
    NotWorking,

    /// <summary>Circuit breaker is open due to repeated failures. Requests are being blocked.</summary>
    CircuitOpen,

    /// <summary>Initial state, no announce attempted yet.</summary>
    Unknown
}

/// <summary>
/// Provides information about a tracker's status and statistics.
/// </summary>
/// <param name="Url">The tracker URL.</param>
/// <param name="Status">The current operational status.</param>
/// <param name="LastAnnounce">The timestamp of the last successful announce.</param>
/// <param name="NextAnnounce">The timestamp of the next scheduled announce.</param>
/// <param name="Interval">The current announce interval in seconds.</param>
/// <param name="ConsecutiveFailures">The number of consecutive announce failures.</param>
/// <param name="LastError">A message describing the last error, if any.</param>
/// <param name="SeedCount">The number of seeds reported by this tracker.</param>
/// <param name="LeechCount">The number of leechers reported by this tracker.</param>
public sealed record TrackerStatus(
    string Url,
    TrackerStatusType Status = TrackerStatusType.Unknown,
    DateTimeOffset LastAnnounce = default,
    DateTimeOffset NextAnnounce = default,
    int Interval = 0,
    int ConsecutiveFailures = 0,
    string? LastError = null,
    uint SeedCount = 0,
    uint LeechCount = 0);

