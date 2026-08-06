using Microsoft.Extensions.Logging;
using PeerSharp.Cli;
using PeerSharp.Config;
using PeerSharp.Clients;
using PeerSharp.Core;
using PeerSharp.Interfaces;

// A sample client, and the harness for diagnosing the library.
//
// It exists for two reasons. PeerSharp had no runnable sample, so the only answer to "how do I use
// this?" was "read the tests". And memory work needs a process that is doing nothing else: measuring
// inside a test host measures the test host, which is exactly how an earlier soak came to report a
// per-peer cost that turned out to be the payload each leecher was buffering.

var options = Options.Parse(args, Console.Error);
if (options is null)
{
    Console.Error.WriteLine();
    Options.PrintUsage(Console.Error);
    return 2;
}

// What is worth watching live and what is worth keeping are different things. With a log file the
// console holds the periodic report and anything that went wrong, while the detail - which runs to
// tens of thousands of lines in a few minutes - goes where it can still be read afterwards. Without
// one, everything goes to the console as before.
var consoleLevel = options.LogPath is null
    ? (options.Verbose ? LogLevel.Trace : LogLevel.Warning)
    : LogLevel.Warning;

// Debug even without -v: a file nobody is watching costs nothing to fill, and a run reported after
// the fact is exactly the one where turning the detail on afterwards is no longer possible.
var fileLevel = options.Verbose ? LogLevel.Trace : LogLevel.Debug;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;

        // Timestamped because the questions this harness gets pointed at are nearly always about
        // when something happened rather than whether it did - how long until the first peer, how
        // long that peer then sat there before it was asked for anything.
        o.TimestampFormat = "HH:mm:ss.fff ";
    });
    builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, consoleLevel);

    if (options.LogPath is { } logPath)
    {
        builder.AddProvider(new FileLoggerProvider(logPath));
        builder.AddFilter<FileLoggerProvider>(null, fileLevel);
    }

    // The floor for both sinks; each provider filters itself down from here.
    builder.SetMinimumLevel(options.LogPath is null ? consoleLevel : fileLevel);
});

Directory.CreateDirectory(options.DownloadPath);

var settings = new Settings
{
    Files = { DefaultDownloadPath = options.DownloadPath },
    Connection =
    {
        EnableLsd = options.LocalDiscovery,
        UpnpPortMapping = options.PortMap,
        NatPmpPortMapping = options.PortMap
    },
    Dht = { Enabled = !options.NoDht },
    Queue =
    {
        Enabled = options.Queue,
        MaxActiveDownloads = options.MaxActiveDownloads
    }
};

await using var engine = ClientEngineFactory.Create(new TorrentClientOptions
{
    LoggerFactory = loggerFactory,
    Settings = settings
});

await engine.InitializeAsync();

Console.WriteLine($"Listening on   : TCP {engine.BoundTcpPort}, UDP {engine.BoundUdpPort}");
Console.WriteLine($"Download path  : {options.DownloadPath}");
if (options.LogPath is { } configuredLogPath)
{
    Console.WriteLine($"Log file       : {Path.GetFullPath(configuredLogPath)} ({fileLevel})");
}

var wallClock = System.Diagnostics.Stopwatch.StartNew();

var torrents = new List<ITorrent>(options.Sources.Count);
foreach (var source in options.Sources)
{
    if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Source         : magnet link");
        torrents.Add(await engine.AddMagnetAsync(
            MagnetLink.Parse(source),
            new AddTorrentOptions
            {
                StartImmediately = !options.Recheck,
                StopAfterMetadata = options.MetadataOnly
            }));
        continue;
    }

    var path = Path.GetFullPath(source);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No such torrent file: {path}");
        return 2;
    }

    Console.WriteLine($"Source         : {path}");
    torrents.Add(await engine.AddTorrentAsync(
        TorrentFile.Load(path),
        new AddTorrentOptions { StartImmediately = !options.Recheck }));
}

if (options.Queue)
{
    Console.WriteLine($"Queue          : on, {options.MaxActiveDownloads} downloading at once");
}

if (options.Recheck)
{
    // Added stopped above: ForceRecheckAsync refuses to run on a live torrent, and a seed that
    // starts before its data is verified would advertise pieces it has not checked.
    Console.WriteLine("Rechecking     : hashing what is already on disk...");
    foreach (var torrent in torrents)
    {
        int have = await torrent.ForceRecheckAsync();
        Console.WriteLine($"                 {have}/{torrent.PieceCount} pieces present  {torrent.Name}");
        await torrent.StartAsync();
    }
}

// Per torrent, which is what the library exposes. With several running this is the setting that
// says whether the engine shares a link sensibly or lets one torrent starve the rest.
foreach (var torrent in torrents)
{
    if (options.DownloadLimitBytesPerSecond is { } down)
    {
        torrent.DownloadLimitBytesPerSecond = down;
    }

    if (options.UploadLimitBytesPerSecond is { } up)
    {
        torrent.UploadLimitBytesPerSecond = up;
    }
}

if (options.Peers.Count > 0)
{
    var endpoints = new List<System.Net.IPEndPoint>();
    foreach (var raw in options.Peers)
    {
        if (System.Net.IPEndPoint.TryParse(raw, out var endpoint))
        {
            endpoints.Add(endpoint);
        }
        else
        {
            Console.Error.WriteLine($"Ignoring unparseable peer address: {raw}");
        }
    }

    // Offered, not forced: these go through the same blocklist, limits and duplicate checks as a
    // peer from any other source, so "accepted" is not the same as "connected".
    int accepted = torrents[0].Peers.Add(endpoints);
    Console.WriteLine($"Peers added    : {accepted} of {endpoints.Count} accepted as candidates (first torrent)");
}

// Ctrl+C should stop the engine rather than kill the process, so a run ends with its files closed
// and its final numbers printed.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Stopping...");
    stopping.Cancel();
};

var reporter = new Reporter(engine, torrents, options, loggerFactory.CreateLogger("Report"));

if (options.MetadataOnly)
{
    if (torrents.All(static t => t.HasMetadata))
    {
        Console.WriteLine("Metadata       : already present, nothing to fetch");
        return 0;
    }

    // Report while waiting rather than blocking silently: when this is slow, the interesting part
    // is what the peer count was doing meanwhile - metadata that takes thirty seconds with a
    // hundred peers connected is a different fault from one that takes thirty seconds to find any.
    var metadataWait = Task.WhenAll(torrents.Select(t => t.WaitForMetadataAsync(stopping.Token)));
    while (!metadataWait.IsCompleted && !stopping.IsCancellationRequested)
    {
        reporter.ReportOnce();
        await Task.WhenAny(metadataWait, Task.Delay(options.ReportInterval, stopping.Token))
            .ConfigureAwait(false);
    }

    await metadataWait.ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine($"Metadata in    : {wallClock.Elapsed.TotalSeconds:F2}s (all {torrents.Count})");
    foreach (var torrent in torrents)
    {
        Console.WriteLine($"  {torrent.PieceCount,6} pieces  {torrent.Name}");
    }

    Console.WriteLine($"Peers at end   : {torrents.Sum(static t => t.Peers.ConnectedCount)}");

    await Task.WhenAll(torrents.Select(static t => t.StopAsync()));
    return 0;
}


bool announcedCompletion = false;
bool stopped = false;
var startedAt = DateTimeOffset.UtcNow;

try
{
    while (!stopping.IsCancellationRequested)
    {
        reporter.ReportOnce();

        if (options.StopAfterSeconds is { } stopAfter
            && !stopped
            && (DateTimeOffset.UtcNow - startedAt).TotalSeconds >= stopAfter)
        {
            stopped = true;
            reporter.ReportSettledHeap("before stop");
            Console.WriteLine("Stopping the torrents...");
            await Task.WhenAll(torrents.Select(static t => t.StopAsync()));

            // Give teardown a moment to finish before asking what is still held.
            await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
            reporter.ReportSettledHeap("after stop ");
        }

        if (torrents.All(static t => t.Finished) && !announcedCompletion)
        {
            announcedCompletion = true;
            Console.WriteLine(options.Seed
                ? "All complete - seeding. Ctrl+C to stop."
                : "All complete.");

            if (!options.Seed)
            {
                break;
            }
        }

        await Task.Delay(options.ReportInterval, stopping.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C during the wait; the run ends normally below.
}

reporter.ReportFinal();

await Task.WhenAll(torrents.Select(static t => t.StopAsync()));
return 0;
