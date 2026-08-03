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
    builder.SetMinimumLevel(options.Verbose ? LogLevel.Trace : LogLevel.Warning);
});

Directory.CreateDirectory(options.DownloadPath);

var settings = new Settings
{
    Files = { DefaultDownloadPath = options.DownloadPath },
    Connection = { EnableLsd = options.LocalDiscovery },
    Dht = { Enabled = !options.NoDht }
};

await using var engine = ClientEngineFactory.Create(new TorrentClientOptions
{
    LoggerFactory = loggerFactory,
    Settings = settings
});

await engine.InitializeAsync();

Console.WriteLine($"Listening on   : TCP {engine.BoundTcpPort}, UDP {engine.BoundUdpPort}");
Console.WriteLine($"Download path  : {options.DownloadPath}");

var wallClock = System.Diagnostics.Stopwatch.StartNew();

ITorrent torrent;
if (options.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Source         : magnet link");
    torrent = await engine.AddMagnetAsync(
        MagnetLink.Parse(options.Source),
        new AddTorrentOptions
        {
            StartImmediately = !options.Recheck,
            StopAfterMetadata = options.MetadataOnly
        });
}
else
{
    var path = Path.GetFullPath(options.Source);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No such torrent file: {path}");
        return 2;
    }

    Console.WriteLine($"Source         : {path}");
    torrent = await engine.AddTorrentAsync(
        TorrentFile.Load(path),
        new AddTorrentOptions { StartImmediately = !options.Recheck });
}

if (options.Recheck)
{
    // Added stopped above: ForceRecheckAsync refuses to run on a live torrent, and a seed that
    // starts before its data is verified would advertise pieces it has not checked.
    Console.WriteLine("Rechecking     : hashing what is already on disk...");
    int have = await torrent.ForceRecheckAsync();
    Console.WriteLine($"                 {have}/{torrent.PieceCount} pieces present");
    await torrent.StartAsync();
}

if (options.DownloadLimitBytesPerSecond is { } down)
{
    torrent.DownloadLimitBytesPerSecond = down;
}

if (options.UploadLimitBytesPerSecond is { } up)
{
    torrent.UploadLimitBytesPerSecond = up;
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
    int accepted = torrent.Peers.Add(endpoints);
    Console.WriteLine($"Peers added    : {accepted} of {endpoints.Count} accepted as candidates");
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

var reporter = new Reporter(engine, torrent, options);

if (options.MetadataOnly)
{
    if (torrent.HasMetadata)
    {
        Console.WriteLine("Metadata       : already present, nothing to fetch");
        return 0;
    }

    // Report while waiting rather than blocking silently: when this is slow, the interesting part
    // is what the peer count was doing meanwhile - metadata that takes thirty seconds with a
    // hundred peers connected is a different fault from one that takes thirty seconds to find any.
    var metadataWait = torrent.WaitForMetadataAsync(stopping.Token);
    while (!metadataWait.IsCompleted && !stopping.IsCancellationRequested)
    {
        reporter.ReportOnce();
        await Task.WhenAny(metadataWait, Task.Delay(options.ReportInterval, stopping.Token))
            .ConfigureAwait(false);
    }

    await metadataWait.ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine($"Metadata in    : {wallClock.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"Name           : {torrent.Name}");
    Console.WriteLine($"Pieces         : {torrent.PieceCount}");
    Console.WriteLine($"Peers at end   : {torrent.Peers.ConnectedCount}");

    await torrent.StopAsync();
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
            Console.WriteLine("Stopping the torrent...");
            await torrent.StopAsync();

            // Give teardown a moment to finish before asking what is still held.
            await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
            reporter.ReportSettledHeap("after stop ");
        }

        if (torrent.Finished && !announcedCompletion)
        {
            announcedCompletion = true;
            Console.WriteLine(options.Seed
                ? "Complete - seeding. Ctrl+C to stop."
                : "Complete.");

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

await torrent.StopAsync();
return 0;
