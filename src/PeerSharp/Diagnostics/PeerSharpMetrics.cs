namespace PeerSharp.Diagnostics;

/// <summary>
/// Identifies the <see cref="System.Diagnostics.Metrics.Meter"/> the engine publishes to, so a host
/// can subscribe to it by name.
/// </summary>
/// <example>
/// With OpenTelemetry:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(metrics => metrics.AddMeter(PeerSharpMetrics.MeterName));
/// </code>
/// </example>
/// <remarks>
/// <para>
/// Every instrument is observable: nothing is measured until a collector asks, and asking costs one
/// pass over the torrent list. There is no cost at all to a process that never subscribes, which is
/// why this needs no enabling switch.
/// </para>
/// <para>
/// Each engine instance publishes its own instruments, tagged with
/// <c>peersharp.engine.id</c> so several engines in one process stay distinguishable.
/// </para>
/// </remarks>
public static class PeerSharpMetrics
{
    /// <summary>The meter name to subscribe to: <c>PeerSharp</c>.</summary>
    public const string MeterName = "PeerSharp";

    /// <summary>Aggregate download rate, in bytes per second.</summary>
    public const string DownloadSpeedInstrument = "peersharp.download.speed";

    /// <summary>Aggregate upload rate, in bytes per second.</summary>
    public const string UploadSpeedInstrument = "peersharp.upload.speed";

    /// <summary>Total bytes downloaded this session.</summary>
    public const string DownloadedInstrument = "peersharp.downloaded";

    /// <summary>Total bytes uploaded this session.</summary>
    public const string UploadedInstrument = "peersharp.uploaded";

    /// <summary>Torrents the engine is managing.</summary>
    public const string TorrentsInstrument = "peersharp.torrents";

    /// <summary>Torrents currently downloading, checking or fetching metadata.</summary>
    public const string ActiveTorrentsInstrument = "peersharp.torrents.active";

    /// <summary>Connected peers across all torrents.</summary>
    public const string ConnectedPeersInstrument = "peersharp.peers.connected";
}
