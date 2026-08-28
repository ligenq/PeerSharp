using PeerSharp.Core;
using PeerSharp.Diagnostics;
using System.Diagnostics.Metrics;

namespace PeerSharp.Internals;

/// <summary>
/// Publishes the engine's aggregate counters to a <see cref="Meter"/>.
///
/// <para>
/// Every instrument here is observable, reading the same aggregate <see cref="EngineStats"/> the
/// public API already exposes. That is deliberate: an engine's hot paths are per block and per
/// message, and putting a counter increment on them would make the measurement a cost of running
/// rather than a cost of watching. Nothing is computed unless a collector polls, and a process that
/// never subscribes pays nothing.
/// </para>
///
/// <para>
/// What this does not cover, and would need real plumbing rather than another observable: per-piece
/// outcomes (verified against hash failures), connection attempts by result, and tracker announce
/// results. Those live inside per-torrent components with no route to engine-level state, and
/// threading one through is a change to those components rather than an addition beside them.
/// </para>
/// </summary>
internal sealed class EngineMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Func<EngineStats?> _readStats;
    private readonly Func<(long Downloaded, long Uploaded)?> _readLifetimeTotals;
    private readonly KeyValuePair<string, object?>[] _tags;
    private AtomicDisposal _disposal = new();

    /// <param name="readStats">
    /// Returns the current aggregate, or <see langword="null"/> when the engine can no longer be
    /// asked - a collector may poll during shutdown, and an instrument callback must not throw.
    /// </param>
    /// <param name="engineId">Distinguishes several engines sharing a process.</param>
    /// <param name="scope">
    /// The object these measurements describe, published as <see cref="Meter.Scope"/>. Every engine
    /// publishes to one meter name, so a listener that subscribes by name alone hears all of them;
    /// the scope is what lets a host attribute a measurement to the engine that produced it, without
    /// having to parse a tag.
    /// </param>
    /// <param name="readLifetimeTotals">
    /// Bytes moved over the engine's whole life. Separate from <paramref name="readStats"/> because
    /// the two answer different questions: <see cref="EngineStats"/> describes the torrents present
    /// now, which is what a dashboard of current activity wants and exactly what a counter must not
    /// be - removing a torrent would take its bytes back out of the total, and a counter that falls
    /// is read as a restart.
    /// </param>
    public EngineMetrics(
        Func<EngineStats?> readStats,
        Func<(long Downloaded, long Uploaded)?> readLifetimeTotals,
        string engineId,
        object? scope = null)
    {
        _readStats = readStats;
        _readLifetimeTotals = readLifetimeTotals;
        _tags = [new KeyValuePair<string, object?>("peersharp.engine.id", engineId)];

        _meter = new Meter(new MeterOptions(PeerSharpMetrics.MeterName)
        {
            Version = typeof(EngineMetrics).Assembly.GetName().Version?.ToString(),
            Scope = scope
        });

        _meter.CreateObservableGauge(
            PeerSharpMetrics.DownloadSpeedInstrument,
            () => Observe(stats => stats.DownloadSpeed),
            unit: "By/s",
            description: "Aggregate download rate across all torrents.");

        _meter.CreateObservableGauge(
            PeerSharpMetrics.UploadSpeedInstrument,
            () => Observe(stats => stats.UploadSpeed),
            unit: "By/s",
            description: "Aggregate upload rate across all torrents.");

        _meter.CreateObservableCounter(
            PeerSharpMetrics.DownloadedInstrument,
            () => ObserveLifetime(totals => totals.Downloaded),
            unit: "By",
            description: "Total bytes downloaded over the engine's lifetime, including by torrents since removed.");

        _meter.CreateObservableCounter(
            PeerSharpMetrics.UploadedInstrument,
            () => ObserveLifetime(totals => totals.Uploaded),
            unit: "By",
            description: "Total bytes uploaded over the engine's lifetime, including by torrents since removed.");

        _meter.CreateObservableGauge(
            PeerSharpMetrics.TorrentsInstrument,
            () => Observe(stats => (long)stats.TorrentCount),
            unit: "{torrent}",
            description: "Torrents the engine is managing.");

        _meter.CreateObservableGauge(
            PeerSharpMetrics.ActiveTorrentsInstrument,
            () => Observe(stats => (long)stats.ActiveTorrents),
            unit: "{torrent}",
            description: "Torrents currently downloading, checking or fetching metadata.");

        _meter.CreateObservableGauge(
            PeerSharpMetrics.ConnectedPeersInstrument,
            () => Observe(stats => (long)stats.TotalPeers),
            unit: "{peer}",
            description: "Connected peers across all torrents.");
    }

    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            _meter.Dispose();
        }
    }

    /// <summary>
    /// Takes one measurement, or none at all if the engine cannot be read. Reporting no measurement
    /// is the honest answer for a disposed engine; reporting zero would look like an idle one.
    /// </summary>
    /// <remarks>
    /// Returns an array rather than iterating lazily. A <c>yield</c> here would compile to a state
    /// machine that the collector owns and disposes, which is a disposable this class creates on
    /// every poll for no benefit - there is at most one measurement to hand back.
    /// </remarks>
    private Measurement<long>[] Observe(Func<EngineStats, long> select)
    {
        EngineStats? stats;
        try
        {
            stats = _readStats();
        }
        catch (ObjectDisposedException)
        {
            // Raced a shutdown between the null check and the read.
            return [];
        }

        return stats == null ? [] : [new Measurement<long>(select(stats), _tags)];
    }

    /// <summary>As <see cref="Observe"/>, over the monotonic lifetime totals.</summary>
    private Measurement<long>[] ObserveLifetime(Func<(long Downloaded, long Uploaded), long> select)
    {
        (long Downloaded, long Uploaded)? totals;
        try
        {
            totals = _readLifetimeTotals();
        }
        catch (ObjectDisposedException)
        {
            return [];
        }

        return totals == null ? [] : [new Measurement<long>(select(totals.Value), _tags)];
    }
}
