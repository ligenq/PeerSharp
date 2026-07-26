using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Clients;
using System.Diagnostics;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Runs the engine against a real public swarm and reports how the other implementations treated it.
///
/// <para>
/// Everything else in this suite is a local swarm talking to itself, which cannot detect the failure
/// mode that decides whether the engine is usable in production: being quietly throttled, choked or
/// dropped by libtorrent, qBittorrent and Transmission peers. Those clients enforce their own
/// expectations about handshakes, reciprocity and message ordering, and a client they dislike still
/// appears to work - it just downloads at a fraction of the speed it should, for reasons no local
/// test can surface. The README already prescribes exactly this discipline for the WebTorrent
/// package; this gives the core engine the same treatment.
/// </para>
///
/// <para>
/// These are diagnostics, not pass/fail gates. Swarm composition is outside our control, so the
/// numbers are the deliverable and the assertions cover only the things that would make the numbers
/// meaningless. They are opt-in twice over: the <c>PeerSharp.Tests.Interop</c> namespace is excluded
/// from every CI job, and each test additionally requires <c>PEERSHARP_SOAK=1</c> because unlike the
/// DHT probes these transfer real data for a long time.
/// </para>
///
/// <para>
/// <b>Content is chosen by the operator, deliberately.</b> Point <c>PEERSHARP_SOAK_TORRENT</c> or
/// <c>PEERSHARP_SOAK_MAGNET</c> at something you have the right to distribute - Linux distribution
/// images are the conventional choice, and are published over BitTorrent by the projects themselves,
/// which also makes them well seeded with a broad mix of client implementations. Nothing is hardcoded,
/// so the tests skip until that choice is made.
/// </para>
/// </summary>
public class RealSwarmSoakTests
{
    private readonly ITestOutputHelper _output;

    public RealSwarmSoakTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Bandwidth ceiling, applied both globally and per torrent so a soak run is a good swarm citizen.
    ///
    /// <para>
    /// These runs are what found this being ignored on plaintext connections: limiting used to live in
    /// the encryption wrapper, so it only applied to peers that negotiated encryption. Expect the
    /// measured rate to sit somewhat above the ceiling regardless - quota bursts up to 3x the limit by
    /// design - which is why the byte budget below exists as a hard stop.
    /// </para>
    /// </summary>
    private const int DefaultRateLimitBytesPerSecond = 2 * 1024 * 1024;

    /// <summary>
    /// Hard ceiling on how much a single run will pull down, enforced here rather than by the engine.
    /// A soak test that cannot bound its own traffic has no business running against real strangers.
    /// </summary>
    private const long DefaultMaxBytes = 1024L * 1024 * 1024;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private static void RequireSoakEnabled()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_SOAK")))
        {
            Assert.Skip(
                "Set PEERSHARP_SOAK=1 to run real-swarm soak tests. These transfer real data over a real " +
                "network for several minutes, which is why they are gated separately from PEERSHARP_INTEROP.");
        }
    }

    private static TimeSpan DurationFromEnvironment(string variable, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
    }

    private static int RateLimitFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("PEERSHARP_SOAK_RATE_BYTES");
        return int.TryParse(raw, out int rate) && rate > 0 ? rate : DefaultRateLimitBytesPerSecond;
    }

    private static long MaxBytesFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("PEERSHARP_SOAK_MAX_BYTES");
        return long.TryParse(raw, out long max) && max > 0 ? max : DefaultMaxBytes;
    }

    /// <summary>
    /// Resolves the first configured torrent, or skips with instructions.
    /// </summary>
    private static async Task<object> ResolveTorrentSourceAsync(CancellationToken cancellationToken)
    {
        return (await ResolveTorrentSourcesAsync(cancellationToken))[0];
    }

    /// <summary>
    /// Resolves every configured torrent. Both variables accept a list separated by <c>;</c> or a
    /// newline, so a run can span several swarms.
    ///
    /// <para>
    /// Variety is worth having: swarm composition differs sharply between projects, and a conclusion
    /// drawn from one torrent is really a conclusion about that torrent's seeders. Different content
    /// sizes also exercise different parts of the engine - a 600 MB image and a 20 MB archive item
    /// have very different piece counts and completion behaviour.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<object>> ResolveTorrentSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = new List<object>();

        foreach (var magnet in SplitConfigured("PEERSHARP_SOAK_MAGNET"))
        {
            sources.Add(MagnetLink.Parse(magnet));
        }

        foreach (var torrent in SplitConfigured("PEERSHARP_SOAK_TORRENT"))
        {
            if (torrent.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                torrent.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                sources.Add(TorrentFile.Parse(await http.GetByteArrayAsync(torrent, cancellationToken)));
            }
            else
            {
                sources.Add(TorrentFile.Parse(await File.ReadAllBytesAsync(torrent, cancellationToken)));
            }
        }

        if (sources.Count == 0)
        {
            Assert.Skip(
                "Set PEERSHARP_SOAK_MAGNET to magnet links, or PEERSHARP_SOAK_TORRENT to .torrent paths or URLs " +
                "(separate several with ';'). Choose content you have the right to distribute - the README lists " +
                "publisher-distributed Linux images and Internet Archive items, which give the broadest mix of " +
                "peer implementations to measure against.");
        }

        return sources;
    }

    private static IEnumerable<string> SplitConfigured(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        char[] separators = [';', (char)10, (char)13];
        return raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Builds an engine with bounded rates and its own scratch directory.
    /// </summary>
    private static (IClientEngine Engine, string DownloadPath) CreateEngine()
    {
        int rate = RateLimitFromEnvironment();

        var settings = new Settings();
        settings.Transfer.MaxDownloadSpeed = (uint)rate;
        settings.Transfer.MaxUploadSpeed = (uint)rate;

        string downloadPath = Path.Combine(Path.GetTempPath(), "peersharp-soak", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);
        settings.Files.DefaultDownloadPath = downloadPath;

        var engine = ClientEngineFactory.Create(new TorrentClientOptions
        {
            Settings = settings,
            LoggerFactory = NullLoggerFactory.Instance
        });

        return (engine, downloadPath);
    }

    private static void CleanUp(string downloadPath)
    {
        try
        {
            if (Directory.Exists(downloadPath))
            {
                Directory.Delete(downloadPath, recursive: true);
            }
        }
        catch (IOException) { /* Best effort - a soak run leaves real files behind. */ }
        catch (UnauthorizedAccessException) { /* As above. */ }
    }

    private async Task<ITorrent> AddAndStartAsync(IClientEngine engine, object source, CancellationToken cancellationToken)
    {
        ITorrent torrent = source switch
        {
            MagnetLink magnet => await engine.AddMagnetAsync(magnet, new AddTorrentOptions(), cancellationToken),
            TorrentFile file => await engine.AddTorrentAsync(file, new AddTorrentOptions(), cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported torrent source {source.GetType().Name}.")
        };

        if (source is MagnetLink)
        {
            _output.WriteLine("Waiting for magnet metadata...");
            await torrent.WaitForMetadataAsync(cancellationToken);
        }

        _output.WriteLine($"Torrent: {torrent.Name} ({torrent.TotalSize:N0} bytes, {torrent.PieceCount} pieces)");

        // Belt and braces on politeness: the per-torrent limits are applied as well as the global ones
        // from Settings.Transfer, so a soak run is capped even if one of the two paths does not bite.
        int rate = RateLimitFromEnvironment();
        torrent.DownloadLimitBytesPerSecond = rate;
        torrent.UploadLimitBytesPerSecond = rate;

        // AddTorrentOptions starts the torrent on add by default, so only start it when something has
        // left it stopped. Starting an already-started torrent throws.
        if (torrent.State == TorrentState.Stopped)
        {
            await torrent.StartAsync(cancellationToken);
        }

        return torrent;
    }

    /// <summary>
    /// Samples the peer list until the deadline, or until <paramref name="stop"/> says otherwise.
    /// </summary>
    private async Task<RealSwarmObserver> ObserveAsync(
        ITorrent torrent,
        TimeSpan duration,
        Func<ITorrent, bool>? stop,
        CancellationToken cancellationToken)
    {
        var observer = new RealSwarmObserver();
        var clock = Stopwatch.StartNew();
        var nextProgressLine = TimeSpan.FromSeconds(30);
        long maxBytes = MaxBytesFromEnvironment();

        while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            observer.Sample(torrent.Peers.GetConnectedPeers());

            if (clock.Elapsed >= nextProgressLine)
            {
                _output.WriteLine(
                    $"t+{(int)clock.Elapsed.TotalSeconds,5}s  peers {torrent.Peers.ConnectedCount,3}  " +
                    $"progress {torrent.Progress * 100,6:F2}%  {torrent.FinishedBytes,14:N0} bytes");
                nextProgressLine += TimeSpan.FromSeconds(30);
            }

            // A hard stop independent of the rate limit, so a run can never pull an unbounded amount of
            // data off strangers' upload capacity even if the limiter regresses again.
            if ((long)torrent.FinishedBytes >= maxBytes)
            {
                _output.WriteLine(
                    $"Stopping at the {maxBytes:N0} byte budget after {clock.Elapsed.TotalSeconds:F0}s " +
                    "(raise PEERSHARP_SOAK_MAX_BYTES to run longer).");
                break;
            }

            if (stop?.Invoke(torrent) == true)
            {
                break;
            }

            await Task.Delay(SampleInterval, cancellationToken);
        }

        return observer;
    }

    /// <summary>
    /// Several real swarms at once, in one engine.
    ///
    /// <para>
    /// Multi-torrent is where engine-wide state gets exercised rather than per-torrent state: shared
    /// bandwidth channels, the connection governor, the peer id, and the single DHT node all serve every
    /// torrent at once. A bug in any of them is invisible to a single-torrent run, and cross-torrent
    /// contamination - one torrent's peers or quota leaking into another's - has nowhere to show up
    /// until two are running side by side.
    /// </para>
    ///
    /// <para>
    /// Skips unless at least two torrents are configured, since with one it would duplicate the
    /// measurement above.
    /// </para>
    /// </summary>
    [Fact(Timeout = 3_600_000)]
    public async Task Interop_MultipleTorrentsAtOnce()
    {
        RequireSoakEnabled();

        var duration = DurationFromEnvironment("PEERSHARP_SOAK_SECONDS", TimeSpan.FromMinutes(10));
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(5));

        var sources = await ResolveTorrentSourcesAsync(cts.Token);
        if (sources.Count < 2)
        {
            Assert.Skip(
                "Configure at least two torrents (separate them with ';') to exercise the multi-torrent paths.");
        }

        var (engine, downloadPath) = CreateEngine();

        try
        {
            await engine.InitializeAsync(cts.Token);

            var torrents = new List<ITorrent>();
            foreach (var source in sources)
            {
                torrents.Add(await AddAndStartAsync(engine, source, cts.Token));
            }

            var observers = new Dictionary<ITorrent, RealSwarmObserver>();
            foreach (var torrent in torrents)
            {
                observers[torrent] = new RealSwarmObserver();
            }

            var clock = Stopwatch.StartNew();
            long maxBytes = MaxBytesFromEnvironment();

            while (clock.Elapsed < duration && !cts.IsCancellationRequested)
            {
                long total = 0;
                foreach (var torrent in torrents)
                {
                    observers[torrent].Sample(torrent.Peers.GetConnectedPeers());
                    total += (long)torrent.FinishedBytes;
                }

                if (total >= maxBytes)
                {
                    _output.WriteLine($"Stopping at the {maxBytes:N0} byte budget across all torrents.");
                    break;
                }

                await Task.Delay(SampleInterval, cts.Token);
            }

            foreach (var torrent in torrents)
            {
                _output.WriteLine(observers[torrent].BuildReport($"{torrent.Name}"));
            }

            var everyPeer = observers.Values.SelectMany(static o => o.Peers.Keys).ToList();
            _output.WriteLine("");
            _output.WriteLine($"torrents running       : {torrents.Count}");
            _output.WriteLine($"peer slots across all  : {everyPeer.Count}");
            _output.WriteLine($"distinct endpoints     : {everyPeer.Distinct().Count()}");
            _output.WriteLine($"engine peers connected : {torrents.Sum(static t => t.Peers.ConnectedCount)}");

            Assert.All(torrents, torrent => Assert.True(
                observers[torrent].Peers.Count > 0,
                $"'{torrent.Name}' reached no peers at all while sharing an engine with {torrents.Count - 1} other " +
                "torrent(s). One torrent starving the others is exactly the cross-contamination this test exists for."));
        }
        finally
        {
            await engine.DisposeAsync();
            CleanUp(downloadPath);
        }
    }

    /// <summary>
    /// The headline measurement: which implementations we meet, and how they treat us.
    ///
    /// <para>
    /// Read the unchoke column first. A client that meets fifty libtorrent peers and is unchoked by
    /// none of them has an interop bug, however healthy the aggregate throughput looks.
    /// </para>
    /// </summary>
    [Fact(Timeout = 3_600_000)]
    public async Task Interop_HowRealClientsTreatUs()
    {
        RequireSoakEnabled();

        var duration = DurationFromEnvironment("PEERSHARP_SOAK_SECONDS", TimeSpan.FromMinutes(10));
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(5));

        var source = await ResolveTorrentSourceAsync(cts.Token);
        var (engine, downloadPath) = CreateEngine();

        try
        {
            await engine.InitializeAsync(cts.Token);
            var torrent = await AddAndStartAsync(engine, source, cts.Token);

            var observer = await ObserveAsync(torrent, duration, stop: null, cts.Token);

            _output.WriteLine(observer.BuildReport($"real swarm interop over {duration.TotalMinutes:F0} minutes"));
            WriteInterpretationNotes(torrent);

            Assert.True(
                observer.Peers.Count > 0,
                "No peers were reached at all, so nothing was measured. Check connectivity, port forwarding and " +
                "that the chosen torrent is still seeded before reading anything into this run.");
        }
        finally
        {
            await engine.DisposeAsync();
            CleanUp(downloadPath);
        }
    }

    /// <summary>
    /// The churn half of the discipline: run long enough for peers to come and go repeatedly, and
    /// confirm the connection pool stays bounded rather than creeping upward.
    /// </summary>
    [Fact(Timeout = 7_200_000)]
    public async Task Soak_ConnectionsStayBoundedUnderChurn()
    {
        RequireSoakEnabled();

        var duration = DurationFromEnvironment("PEERSHARP_SOAK_CHURN_SECONDS", TimeSpan.FromMinutes(30));
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(5));

        var source = await ResolveTorrentSourceAsync(cts.Token);
        var (engine, downloadPath) = CreateEngine();

        try
        {
            await engine.InitializeAsync(cts.Token);
            var torrent = await AddAndStartAsync(engine, source, cts.Token);

            int configuredMax = (int)engine.Settings.Connection.MaxConnections;
            var observer = await ObserveAsync(torrent, duration, stop: null, cts.Token);

            _output.WriteLine(observer.BuildReport($"real swarm churn soak over {duration.TotalMinutes:F0} minutes"));
            _output.WriteLine($"configured connection ceiling : {configuredMax}");
            _output.WriteLine($"peak concurrent peers observed: {observer.PeakConcurrentPeers}");
            _output.WriteLine($"distinct peers over the run   : {observer.Peers.Count}");

            Assert.True(observer.Peers.Count > 0, "No peers were reached, so churn was not exercised.");

            // The real leak this catches is connections accumulating faster than they are reaped. The
            // engine's own ceiling is the contract, so exceeding it is a defect regardless of swarm size.
            Assert.True(
                observer.PeakConcurrentPeers <= configuredMax,
                $"Concurrent peers peaked at {observer.PeakConcurrentPeers}, above the configured ceiling of " +
                $"{configuredMax}. Connections are not being reaped.");

            // Churn only happened if the swarm turned over. Without this the bound above proves nothing.
            _output.WriteLine(
                observer.Peers.Count > observer.PeakConcurrentPeers
                    ? "Swarm turned over during the run, so the bound above was actually exercised."
                    : "No turnover observed - every peer met stayed connected, so this run did not test churn.");
        }
        finally
        {
            await engine.DisposeAsync();
            CleanUp(downloadPath);
        }
    }

    /// <summary>
    /// End to end: can the engine actually finish a download from strangers? Everything else measures
    /// symptoms; this measures the outcome.
    ///
    /// <para>
    /// Point this at something small. The default budget is generous but a distribution image will not
    /// finish inside it on a rate-limited connection, in which case the test reports how far it got.
    /// </para>
    /// </summary>
    [Fact(Timeout = 7_200_000)]
    public async Task Interop_DownloadRunsToCompletion()
    {
        RequireSoakEnabled();

        var duration = DurationFromEnvironment("PEERSHARP_SOAK_COMPLETION_SECONDS", TimeSpan.FromMinutes(30));
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(5));

        var source = await ResolveTorrentSourceAsync(cts.Token);
        var (engine, downloadPath) = CreateEngine();

        try
        {
            await engine.InitializeAsync(cts.Token);
            var torrent = await AddAndStartAsync(engine, source, cts.Token);

            var clock = Stopwatch.StartNew();
            var observer = await ObserveAsync(
                torrent,
                duration,
                stop: static t => t.Progress >= 1.0f,
                cts.Token);

            bool finished = torrent.Progress >= 1.0f;

            _output.WriteLine(observer.BuildReport("real swarm completion run"));
            _output.WriteLine($"completed              : {finished}");
            _output.WriteLine($"progress               : {torrent.Progress * 100:F2}%");
            _output.WriteLine($"bytes                  : {torrent.FinishedBytes:N0} of {torrent.TotalSize:N0}");
            _output.WriteLine($"elapsed                : {clock.Elapsed}");

            if (clock.Elapsed.TotalSeconds > 1)
            {
                _output.WriteLine($"average rate           : {torrent.FinishedBytes / clock.Elapsed.TotalSeconds:N0} bytes/s");
            }

            // Not asserted as completion: whether a given torrent finishes inside the budget depends on
            // its size, the rate limit and how well seeded it is, none of which are ours to control.
            // Asserting it would produce a test that fails for reasons unrelated to the engine.
            Assert.True(
                torrent.FinishedBytes > 0,
                "Not a single byte arrived from the swarm. That is an engine problem rather than a slow run: " +
                "peers were reachable but no piece data was ever delivered.");

            if (!finished)
            {
                _output.WriteLine(
                    "Did not finish inside the budget. Raise PEERSHARP_SOAK_COMPLETION_SECONDS, raise " +
                    "PEERSHARP_SOAK_RATE_BYTES, or point the run at a smaller torrent.");
            }
        }
        finally
        {
            await engine.DisposeAsync();
            CleanUp(downloadPath);
        }
    }

    /// <summary>
    /// Records the caveats needed to read the numbers honestly, next to the numbers themselves.
    /// </summary>
    private void WriteInterpretationNotes(ITorrent torrent)
    {
        _output.WriteLine("");
        _output.WriteLine("trackers:");
        foreach (var tracker in torrent.Trackers.GetTrackers())
        {
            _output.WriteLine(
                $"  {tracker.Status,-12} seeds {tracker.SeedCount,6} leechers {tracker.LeechCount,6}  " +
                $"failures {tracker.ConsecutiveFailures}  {tracker.Url}");
        }

        _output.WriteLine("");
        _output.WriteLine("how to read this:");
        _output.WriteLine("  - 'unchoked us' is the throttling signal. A whole implementation at 0% is an interop bug;");
        _output.WriteLine("    a low rate across every implementation usually means we gave them no reason to reciprocate.");
        _output.WriteLine("  - Tit-for-tat confounds a leech-only run. If 'we served' is near zero everywhere, the low");
        _output.WriteLine("    unchoke rates say more about our upload than about their opinion of us. Re-run while");
        _output.WriteLine("    seeding before concluding anything.");
        _output.WriteLine("  - 'seen once' counts connections that did not outlive one sampling interval. A cluster of");
        _output.WriteLine("    those against one implementation is what a post-handshake rejection looks like from here.");
        _output.WriteLine("  - Swarm composition is not controlled. Compare runs against each other, not against a target.");
    }
}
