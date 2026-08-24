namespace PeerSharp.Cli;

/// <summary>
/// What the sample was asked to do. Parsed by hand rather than with a command line library: a sample
/// whose first dependency is an argument parser teaches the wrong thing about the library it exists
/// to demonstrate.
/// </summary>
internal sealed record Options
{
    /// <summary>
    /// The torrents or magnets to run. More than one is the case worth exercising: everything that
    /// arbitrates between torrents - the queue, the global connection governor, bandwidth allocation -
    /// only does anything when there are several.
    /// </summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required string DownloadPath { get; init; }

    /// <summary>Print a diagnostics block on each report, not just progress.</summary>
    public bool Diagnostics { get; init; }

    /// <summary>Keep running once complete, so a seeding process can be profiled too.</summary>
    public bool Seed { get; init; }

    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int? DownloadLimitBytesPerSecond { get; init; }

    public int? UploadLimitBytesPerSecond { get; init; }

    public bool Verbose { get; init; }

    /// <summary>Find peers on the local network (BEP 14), which is how two instances on one
    /// machine find each other without a tracker.</summary>
    public bool LocalDiscovery { get; init; }

    /// <summary>Hash-check the files already on disk before starting, which is what turns an
    /// existing download into a seed.</summary>
    public bool Recheck { get; init; }

    /// <summary>Run without the DHT, which is what isolates a local measurement from the
    /// public network.</summary>
    public bool NoDht { get; init; }

    /// <summary>
    /// Ask the router to forward the listening port (UPnP and NAT-PMP), off by default in the library
    /// and here. Worth turning on to soak the incoming path: without a forwarded port most connections
    /// are ones this process dialled, and everything peers tell us about themselves - where they
    /// listen, what they support - only arrives on connections they opened.
    /// </summary>
    public bool PortMap { get; init; }

    /// <summary>
    /// Where to write the full log. When set, the console keeps only the periodic report and anything
    /// at warning or above, and the detail goes here - which is the only way to still have the start of
    /// a run when something goes wrong several minutes in.
    /// </summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// Turn on the torrent queue, which is off by default and has never run against real torrents.
    /// It stops torrents past <see cref="MaxActiveDownloads"/> and restarts them as others finish.
    /// </summary>
    public bool Queue { get; init; }

    /// <summary>How many torrents the queue lets download at once. Ignored unless <see cref="Queue"/>.</summary>
    public int MaxActiveDownloads { get; init; } = 3;

    /// <summary>
    /// The port to listen on, when the default would collide. Two instances on one machine is how a
    /// transfer gets watched from both ends, and they cannot share a port.
    /// </summary>
    public ushort? Port { get; init; }

    /// <summary>
    /// Directory to keep resume data in. Saved when the run ends and loaded when it starts, which is
    /// what lets a restart pick up where the last one stopped instead of rehashing everything - the
    /// path a real client takes every time it is closed and reopened.
    /// </summary>
    public string? ResumeDir { get; init; }

    /// <summary>
    /// End the run cleanly after this many seconds, exactly as Ctrl+C would. Killing the process from
    /// outside instead - which is what a script reaches for - loses everything that happens on the way
    /// out: the final totals, and any state the run was supposed to save.
    /// </summary>
    public int? RunForSeconds { get; init; }

    /// <summary>Peers to try in addition to discovery, as host:port.</summary>
    public IReadOnlyList<string> Peers { get; init; } = [];

    /// <summary>
    /// Fetch a magnet's metadata, report how long it took, and stop without downloading anything.
    /// Metadata is the whole of a magnet's startup latency, and it is worth being able to time it
    /// on its own rather than inferring it from where a transfer appears to begin.
    /// </summary>
    public bool MetadataOnly { get; init; }

    /// <summary>
    /// Stop the torrent after this many seconds and keep reporting, which is how you see whether
    /// stopping actually gives memory back.
    /// </summary>
    public int? StopAfterSeconds { get; init; }

    /// <summary>
    /// Treat a line or EOF on standard input as a clean shutdown request. This is primarily for
    /// automation which needs the same final reporting and disposal path as Ctrl+C without sending
    /// platform-specific console signals.
    /// </summary>
    public bool ControlStdin { get; init; }

    public static Options? Parse(string[] args, TextWriter error)
    {
        var sources = new List<string>();
        var downloadPath = Directory.GetCurrentDirectory();
        bool diagnostics = false;
        bool seed = false;
        bool verbose = false;
        bool lsd = false;
        bool recheck = false;
        bool noDht = false;
        bool metadataOnly = false;
        bool portMap = false;
        string? logPath = null;
        bool queue = false;
        ushort? port = null;
        string? resumeDir = null;
        int? runFor = null;
        int maxActiveDownloads = 3;
        var peers = new List<string>();
        int? stopAfter = null;
        bool controlStdin = false;
        var interval = TimeSpan.FromSeconds(5);
        int? down = null;
        int? up = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--diagnostics" or "-d":
                    diagnostics = true;
                    break;

                case "--seed" or "-s":
                    seed = true;
                    break;

                case "--verbose" or "-v":
                    verbose = true;
                    break;

                case "--lsd":
                    lsd = true;
                    break;

                case "--recheck":
                    recheck = true;
                    break;

                case "--no-dht":
                    noDht = true;
                    break;

                case "--metadata-only":
                    metadataOnly = true;
                    break;

                case "--port-map":
                    portMap = true;
                    break;

                case "--port":
                    if (!TryTake(args, ref i, out var rawPort) || !ushort.TryParse(rawPort, out ushort parsedPort))
                    {
                        error.WriteLine("--port needs a number from 0 to 65535 (0 lets the OS choose).");
                        return null;
                    }

                    port = parsedPort;
                    break;

                case "--resume":
                    if (!TryTake(args, ref i, out var rawResume))
                    {
                        error.WriteLine("--resume needs a directory.");
                        return null;
                    }

                    resumeDir = rawResume;
                    break;

                case "--run-for":
                    if (!TryTake(args, ref i, out var rawRunFor) || !int.TryParse(rawRunFor, out int runForSeconds) || runForSeconds <= 0)
                    {
                        error.WriteLine("--run-for needs a positive number of seconds.");
                        return null;
                    }

                    runFor = runForSeconds;
                    break;

                case "--queue":
                    queue = true;
                    break;

                case "--max-active":
                    if (!TryTake(args, ref i, out var rawActive) || !int.TryParse(rawActive, out maxActiveDownloads) || maxActiveDownloads <= 0)
                    {
                        error.WriteLine("--max-active needs a positive number of torrents.");
                        return null;
                    }

                    queue = true;
                    break;

                case "--log":
                    if (!TryTake(args, ref i, out var rawLogPath))
                    {
                        error.WriteLine("--log needs a file path.");
                        return null;
                    }

                    logPath = rawLogPath;
                    break;

                case "--stop-after":
                    if (!TryTake(args, ref i, out var rawStop) || !int.TryParse(rawStop, out int stopSeconds) || stopSeconds <= 0)
                    {
                        error.WriteLine("--stop-after needs a positive number of seconds.");
                        return null;
                    }

                    stopAfter = stopSeconds;
                    break;

                case "--control-stdin":
                    controlStdin = true;
                    break;

                case "--peer":
                    if (!TryTake(args, ref i, out var peer))
                    {
                        error.WriteLine("--peer needs an address as host:port.");
                        return null;
                    }

                    peers.Add(peer);
                    break;

                case "--out" or "-o":
                    if (!TryTake(args, ref i, out var path))
                    {
                        error.WriteLine("--out needs a directory.");
                        return null;
                    }

                    downloadPath = path;
                    break;

                case "--interval":
                    if (!TryTake(args, ref i, out var raw) || !int.TryParse(raw, out int seconds) || seconds <= 0)
                    {
                        error.WriteLine("--interval needs a positive number of seconds.");
                        return null;
                    }

                    interval = TimeSpan.FromSeconds(seconds);
                    break;

                case "--down":
                    if (!TryTakeRate(args, ref i, out down))
                    {
                        error.WriteLine("--down needs a rate in KiB/s.");
                        return null;
                    }

                    break;

                case "--up":
                    if (!TryTakeRate(args, ref i, out up))
                    {
                        error.WriteLine("--up needs a rate in KiB/s.");
                        return null;
                    }

                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error.WriteLine($"Unknown option: {arg}");
                        return null;
                    }

                    sources.Add(arg);
                    break;
            }
        }

        if (sources.Count == 0)
        {
            error.WriteLine("A .torrent path or magnet link is required.");
            return null;
        }

        return new Options
        {
            Sources = sources,
            DownloadPath = Path.GetFullPath(downloadPath),
            Diagnostics = diagnostics,
            Seed = seed,
            Verbose = verbose,
            LocalDiscovery = lsd,
            Recheck = recheck,
            NoDht = noDht,
            MetadataOnly = metadataOnly,
            PortMap = portMap,
            LogPath = logPath,
            Port = port,
            ResumeDir = resumeDir,
            RunForSeconds = runFor,
            Queue = queue,
            MaxActiveDownloads = maxActiveDownloads,
            Peers = peers,
            StopAfterSeconds = stopAfter,
            ControlStdin = controlStdin,
            ReportInterval = interval,
            DownloadLimitBytesPerSecond = down,
            UploadLimitBytesPerSecond = up
        };
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            peersharp-cli - a sample client, and the harness for diagnosing the library

            usage: peersharp-cli <torrent-file|magnet> [more...] [options]

              -o, --out <dir>      where to write data (default: current directory)
              -d, --diagnostics    report heap, peers and queue depths each interval
              -s, --seed           keep running after completion, so seeding can be profiled
              -v, --verbose        library logging at trace level
                  --lsd            discover peers on the local network (BEP 14)
                  --recheck        hash-check existing files first, to seed what is already there
                  --no-dht         disable the DHT, isolating a run from the public network
                  --metadata-only  fetch a magnet's metadata, time it, and stop before downloading
                  --port-map       ask the router to forward the port (UPnP, NAT-PMP), for seeding
                  --log <file>     write the full log here; the console keeps reports and warnings
                  --port <n>       listen here instead of the default, for two instances at once
                  --resume <dir>   save resume data here on exit and reload it on start
                  --run-for <s>    end cleanly after this long, as Ctrl+C would
                  --queue          enable the torrent queue (off by default)
                  --max-active <n> torrents downloading at once; implies --queue (default 3)
                  --peer <ip:port> try this peer as well as any found by discovery (repeatable)
                  --stop-after <s> stop the torrent after this long and keep reporting
                  --control-stdin  stop cleanly when stdin receives a line or reaches EOF
                  --interval <s>   seconds between reports (default: 5)
                  --down <KiB/s>   download rate limit
                  --up <KiB/s>     upload rate limit

            Memory work wants a real workload rather than a test host, so this runs as its own
            process: dotnet-counters, dotnet-gcdump and dotnet-trace can all attach to it.
            """);
    }

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryTakeRate(string[] args, ref int index, out int? rate)
    {
        rate = null;
        if (!TryTake(args, ref index, out var raw)
            || !int.TryParse(raw, out int kib)
            || kib <= 0
            || kib > int.MaxValue / 1024)
        {
            return false;
        }

        rate = kib * 1024;
        return true;
    }
}
