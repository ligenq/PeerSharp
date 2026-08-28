using PeerSharp.Core;
using PeerSharp.Diagnostics;
using PeerSharp.Internals;
using System.Diagnostics.Metrics;

namespace PeerSharp.Tests.Core;

/// <summary>
/// The metrics surface a host subscribes to.
///
/// <para>
/// The engine had a good <c>ILogger</c> story and an alert queue, and neither gives anyone a
/// dashboard. These check the two properties that make the instruments safe to ship: that they read
/// the same aggregate the public API already exposes, and that a collector polling during shutdown
/// gets silence rather than an exception or a fabricated zero.
/// </para>
/// </summary>
public class EngineMetricsTests
{
    private static readonly EngineStats SampleStats = new(
        DownloadSpeed: 1_024,
        UploadSpeed: 512,
        TotalDownloaded: 4_096,
        TotalUploaded: 2_048,
        TorrentCount: 3,
        ActiveTorrents: 2,
        TotalPeers: 17);

    /// <summary>
    /// Collects one round of measurements from a single engine's meter.
    ///
    /// <para>
    /// Filtered by <see cref="Meter.Scope"/> rather than by meter name. Every engine publishes under
    /// the one name, so a name-only listener also hears every other engine alive in the process -
    /// which, in a suite that runs test collections in parallel, is a source of failures that have
    /// nothing to do with the code under test. This is the same problem a host with two engines has,
    /// and the scope is the answer for both.
    /// </para>
    /// </summary>
    private static Dictionary<string, long> Collect(
        Func<EngineStats?> readStats,
        out List<KeyValuePair<string, object?>> tags,
        Func<(long Downloaded, long Uploaded)?>? readLifetimeTotals = null)
    {
        var scope = new object();
        var measurements = new Dictionary<string, long>();
        var collectedTags = new List<KeyValuePair<string, object?>>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, scope))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, instrumentTags, _) =>
        {
            measurements[instrument.Name] = value;
            collectedTags.AddRange(instrumentTags.ToArray());
        });

        readLifetimeTotals ??= () =>
        {
            var stats = readStats();
            return stats == null ? null : (stats.TotalDownloaded, stats.TotalUploaded);
        };

        using var metrics = new EngineMetrics(readStats, readLifetimeTotals, "test-engine", scope);
        listener.Start();
        listener.RecordObservableInstruments();

        tags = collectedTags;
        return measurements;
    }

    [Fact]
    public void EveryInstrument_ReportsTheEngineAggregate()
    {
        var measurements = Collect(() => SampleStats, out _);

        Assert.Equal(1_024, measurements[PeerSharpMetrics.DownloadSpeedInstrument]);
        Assert.Equal(512, measurements[PeerSharpMetrics.UploadSpeedInstrument]);
        Assert.Equal(4_096, measurements[PeerSharpMetrics.DownloadedInstrument]);
        Assert.Equal(2_048, measurements[PeerSharpMetrics.UploadedInstrument]);
        Assert.Equal(3, measurements[PeerSharpMetrics.TorrentsInstrument]);
        Assert.Equal(2, measurements[PeerSharpMetrics.ActiveTorrentsInstrument]);
        Assert.Equal(17, measurements[PeerSharpMetrics.ConnectedPeersInstrument]);
    }

    [Fact]
    public void MeasurementsCarryTheEngineId()
    {
        // Two engines in one process would otherwise produce one indistinguishable series.
        Collect(() => SampleStats, out var tags);

        Assert.Contains(tags, tag => tag.Key == "peersharp.engine.id" && (string?)tag.Value == "test-engine");
    }

    [Fact]
    public void AShutDownEngine_ReportsNothingRatherThanZero()
    {
        // A collector can poll at any moment, including during shutdown. Zero would read as an idle
        // engine on a dashboard, which is a different and wrong statement.
        var measurements = Collect(() => null, out _);

        Assert.Empty(measurements);
    }

    [Fact]
    public void AnEngineDisposedMidPoll_DoesNotThrowThroughTheCollector()
    {
        // The null check and the read cannot be made atomic from outside, so the race has to be
        // survivable rather than prevented. An instrument callback that throws takes out the host's
        // whole collection cycle, not just ours.
        var measurements = Collect(() => throw new ObjectDisposedException(nameof(ClientEngine)), out _);

        Assert.Empty(measurements);
    }

    [Fact]
    public void ByteTotalsComeFromLifetimeFigures_NotTheCurrentTorrentList()
    {
        // The two sources are deliberately separate. GetStats sums the torrents registered right now,
        // which is the right answer for a rate or a peer count and the wrong one for a counter:
        // removing a torrent would take its bytes back out and a falling counter reads to a metrics
        // backend as a process restart, producing a spurious jump rather than a gap.
        var measurements = Collect(
            () => SampleStats,
            out _,
            readLifetimeTotals: () => (9_000, 8_000));

        Assert.Equal(9_000, measurements[PeerSharpMetrics.DownloadedInstrument]);
        Assert.Equal(8_000, measurements[PeerSharpMetrics.UploadedInstrument]);

        // The gauges still describe the present.
        Assert.Equal(1_024, measurements[PeerSharpMetrics.DownloadSpeedInstrument]);
        Assert.Equal(3, measurements[PeerSharpMetrics.TorrentsInstrument]);
    }

    [Fact]
    public async Task RemovingATorrent_DoesNotMakeTheByteCountersGoBackwards()
    {
        // The property a counter has to have, exercised through a real engine: add a torrent, take a
        // reading, remove it, take another. Before lifetime totals existed the second reading was
        // lower than the first.
        string path = Path.Combine(Path.GetTempPath(), "PeerSharpMetricsMonotonic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var settings = new Settings
            {
                Files = { DefaultDownloadPath = path },
                Dht = { Enabled = false },
                Session = { Enabled = false }
            };

            await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });

            var torrentFile = new TorrentFileBuilder()
                .WithName("counted.bin")
                .WithPieceLength(16384)
                .AddFile("counted.bin", new byte[16384])
                .Build();

            var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions(path) { StartImmediately = false });

            var before = engine.GetLifetimeTotals();
            await engine.RemoveTorrentAsync(torrent, RemoveOptions.None, TestContext.Current.CancellationToken);
            var after = engine.GetLifetimeTotals();

            Assert.True(after.Downloaded >= before.Downloaded, $"downloaded fell from {before.Downloaded} to {after.Downloaded}");
            Assert.True(after.Uploaded >= before.Uploaded, $"uploaded fell from {before.Uploaded} to {after.Uploaded}");

            // And the engine no longer counts it as present, which is what GetStats is for.
            Assert.Equal(0, engine.GetStats().TorrentCount);
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ARealEngine_PublishesItsInstruments()
    {
        // The wiring, not the arithmetic: an engine that never constructs its metrics would pass
        // every test above. Scoped to this engine so other engines running in parallel collections
        // cannot answer for it.
        var measurements = new Dictionary<string, long>();

        string path = Path.Combine(Path.GetTempPath(), "PeerSharpMetrics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var settings = new Settings
            {
                Files = { DefaultDownloadPath = path },
                Dht = { Enabled = false },
                Session = { Enabled = false }
            };

            await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });

            // Built after the engine: Start replays the instruments that already exist, and it only
            // does that once, so a listener created earlier would never see this engine's.
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter.Scope, engine))
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements[instrument.Name] = value);
            listener.Start();

            listener.RecordObservableInstruments();
            Assert.Equal(0, measurements[PeerSharpMetrics.TorrentsInstrument]);
            Assert.Equal(0, measurements[PeerSharpMetrics.ConnectedPeersInstrument]);

            await engine.DisposeAsync();

            // The engine's meter went with it, so a later poll reports nothing for this engine.
            measurements.Clear();
            listener.RecordObservableInstruments();
            Assert.Empty(measurements);
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { /* best effort */ }
        }
    }
}
