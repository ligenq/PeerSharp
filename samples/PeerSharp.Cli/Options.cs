namespace PeerSharp.Cli;

/// <summary>
/// What the sample was asked to do. Parsed by hand rather than with a command line library: a sample
/// whose first dependency is an argument parser teaches the wrong thing about the library it exists
/// to demonstrate.
/// </summary>
internal sealed record Options
{
    public required string Source { get; init; }

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

    /// <summary>Peers to try in addition to discovery, as host:port.</summary>
    public IReadOnlyList<string> Peers { get; init; } = [];

    /// <summary>
    /// Stop the torrent after this many seconds and keep reporting, which is how you see whether
    /// stopping actually gives memory back.
    /// </summary>
    public int? StopAfterSeconds { get; init; }

    public static Options? Parse(string[] args, TextWriter error)
    {
        string? source = null;
        var downloadPath = Directory.GetCurrentDirectory();
        bool diagnostics = false;
        bool seed = false;
        bool verbose = false;
        bool lsd = false;
        bool recheck = false;
        bool noDht = false;
        var peers = new List<string>();
        int? stopAfter = null;
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

                case "--stop-after":
                    if (!TryTake(args, ref i, out var rawStop) || !int.TryParse(rawStop, out int stopSeconds) || stopSeconds <= 0)
                    {
                        error.WriteLine("--stop-after needs a positive number of seconds.");
                        return null;
                    }

                    stopAfter = stopSeconds;
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

                    if (source is not null)
                    {
                        error.WriteLine("Only one torrent or magnet may be given.");
                        return null;
                    }

                    source = arg;
                    break;
            }
        }

        if (source is null)
        {
            error.WriteLine("A .torrent path or magnet link is required.");
            return null;
        }

        return new Options
        {
            Source = source,
            DownloadPath = Path.GetFullPath(downloadPath),
            Diagnostics = diagnostics,
            Seed = seed,
            Verbose = verbose,
            LocalDiscovery = lsd,
            Recheck = recheck,
            NoDht = noDht,
            Peers = peers,
            StopAfterSeconds = stopAfter,
            ReportInterval = interval,
            DownloadLimitBytesPerSecond = down,
            UploadLimitBytesPerSecond = up
        };
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            peersharp-cli - a sample client, and the harness for diagnosing the library

            usage: peersharp-cli <torrent-file|magnet> [options]

              -o, --out <dir>      where to write data (default: current directory)
              -d, --diagnostics    report heap, peers and queue depths each interval
              -s, --seed           keep running after completion, so seeding can be profiled
              -v, --verbose        library logging at debug level
                  --lsd            discover peers on the local network (BEP 14)
                  --recheck        hash-check existing files first, to seed what is already there
                  --no-dht         disable the DHT, isolating a run from the public network
                  --peer <ip:port> try this peer as well as any found by discovery (repeatable)
                  --stop-after <s> stop the torrent after this long and keep reporting
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
        if (!TryTake(args, ref index, out var raw) || !int.TryParse(raw, out int kib) || kib <= 0)
        {
            return false;
        }

        rate = kib * 1024;
        return true;
    }
}
