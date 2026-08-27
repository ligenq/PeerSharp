using System.Globalization;

namespace PeerSharp.EndToEnd;

internal sealed record BenchmarkOptions
{
    public required string Command { get; init; }
    public required string RepositoryRoot { get; init; }
    public required string LibtorrentRoot { get; init; }
    public required string ArtifactRoot { get; init; }
    public required IReadOnlyList<string> Engines { get; init; }
    public required IReadOnlyList<string> Modes { get; init; }
    public required IReadOnlyList<string> Variants { get; init; }
    public required IReadOnlyList<string> LibtorrentBackends { get; init; }
    public int SizeMiB { get; init; } = 64;
    public int FileCount { get; init; } = 4;
    public int PeerCount { get; init; } = 4;

    /// <summary>
    /// How many 16 KiB blocks the shared peer transfers before dropping the connection and coming
    /// back, or zero to leave connections alone. Every other trial runs on connections that are made
    /// once and kept, which is the one thing a real swarm never does.
    /// </summary>
    /// <remarks>
    /// Note the direction: this is a period, not a rate, so a larger number churns less. Two is
    /// severe - a reconnect every thirty-two kilobytes, which measured out at about eleven hundred
    /// connections a second. connection_tester's own help calls it "reconnects per second", which its
    /// code does not agree with: it closes on <c>blocks_sent % churn == 0</c>.
    /// </remarks>
    public int ChurnBlocks { get; init; }

    /// <summary>
    /// Whether the shared peer corrupts some of the pieces it sends. Only the modes where it uploads
    /// - download and dual - are affected, since that is the direction the engine has to verify.
    /// </summary>
    public bool Corrupt { get; init; }
    public int Iterations { get; init; } = 3;
    public int Warmups { get; init; } = 1;
    public int TimeoutSeconds { get; init; } = 180;
    public int RandomSeed { get; init; } = 1741;
    public bool SkipBuild { get; init; }
    public bool KeepRunData { get; init; }
    public bool Help { get; init; }

    public static BenchmarkOptions? Parse(string[] args, TextWriter error)
    {
        string repositoryRoot = FindRepositoryRoot();
        string command = args.Length == 0 || args[0].StartsWith('-') ? "run" : args[0].ToLowerInvariant();
        int start = command == "run" && (args.Length == 0 || args[0].StartsWith('-')) ? 0 : 1;

        string libtorrentRoot = Environment.GetEnvironmentVariable("PEERSHARP_LIBTORRENT_ROOT")
            ?? Path.GetFullPath(Path.Combine(repositoryRoot, "..", "libtorrent"));
        string artifactRoot = Path.Combine(repositoryRoot, "artifacts", "peersharp-e2e");
        IReadOnlyList<string> engines = ["peersharp", "libtorrent"];
        IReadOnlyList<string> modes = ["download"];
        IReadOnlyList<string> variants = ["v1"];
        IReadOnlyList<string> backends = ["mmap"];
        int sizeMiB = 64;
        int fileCount = 4;
        int peerCount = 4;
        int iterations = 3;
        int warmups = 1;
        int timeoutSeconds = 180;
        int randomSeed = 1741;
        int churnBlocks = 0;
        bool corrupt = false;
        bool skipBuild = false;
        bool keepRunData = false;
        bool help = false;

        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    break;
                case "--churn":
                    if (!TakeNonNegative(args, ref i, error, arg, out churnBlocks)) return null;
                    break;
                case "--corrupt":
                    corrupt = true;
                    break;
                case "--skip-build":
                    skipBuild = true;
                    break;
                case "--keep-run-data":
                    keepRunData = true;
                    break;
                case "--libtorrent-root":
                    if (!Take(args, ref i, error, arg, out libtorrentRoot)) return null;
                    libtorrentRoot = Path.GetFullPath(libtorrentRoot);
                    break;
                case "--artifacts":
                    if (!Take(args, ref i, error, arg, out artifactRoot)) return null;
                    artifactRoot = Path.GetFullPath(artifactRoot);
                    break;
                case "--engines":
                    if (!TakeList(args, ref i, error, arg, ["peersharp", "libtorrent"], out engines)) return null;
                    break;
                case "--modes":
                    if (!TakeList(args, ref i, error, arg, ["download", "upload", "dual", "metadata"], out modes)) return null;
                    break;
                case "--variants":
                    if (!TakeList(args, ref i, error, arg, ["v1", "v2", "hybrid"], out variants)) return null;
                    break;
                case "--libtorrent-backends":
                    if (!TakeList(args, ref i, error, arg, ["mmap", "pread", "posix"], out backends)) return null;
                    break;
                case "--size-mib":
                    if (!TakePositive(args, ref i, error, arg, out sizeMiB)) return null;
                    break;
                case "--files":
                    if (!TakePositive(args, ref i, error, arg, out fileCount)) return null;
                    break;
                case "--peers":
                    if (!TakePositive(args, ref i, error, arg, out peerCount)) return null;
                    break;
                case "--iterations":
                    if (!TakePositive(args, ref i, error, arg, out iterations)) return null;
                    break;
                case "--warmups":
                    if (!TakeNonNegative(args, ref i, error, arg, out warmups)) return null;
                    break;
                case "--timeout":
                    if (!TakePositive(args, ref i, error, arg, out timeoutSeconds)) return null;
                    break;
                case "--seed":
                    if (!TakeNonNegative(args, ref i, error, arg, out randomSeed)) return null;
                    break;
                default:
                    error.WriteLine($"Unknown argument: {arg}");
                    return null;
            }
        }

        if (command is not ("run" or "build" or "doctor"))
        {
            error.WriteLine($"Unknown command: {command}");
            return null;
        }

        return new BenchmarkOptions
        {
            Command = command,
            RepositoryRoot = repositoryRoot,
            LibtorrentRoot = libtorrentRoot,
            ArtifactRoot = artifactRoot,
            Engines = engines,
            Modes = modes,
            Variants = variants,
            LibtorrentBackends = backends,
            SizeMiB = sizeMiB,
            FileCount = fileCount,
            PeerCount = peerCount,
            ChurnBlocks = churnBlocks,
            Corrupt = corrupt,
            Iterations = iterations,
            Warmups = warmups,
            TimeoutSeconds = timeoutSeconds,
            RandomSeed = randomSeed,
            SkipBuild = skipBuild,
            KeepRunData = keepRunData,
            Help = help
        };
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            peersharp-e2e - controlled PeerSharp versus libtorrent end-to-end benchmarks

            usage:
              peersharp-e2e doctor [options]
              peersharp-e2e build [options]
              peersharp-e2e run [options]

            commands:
              doctor              inspect prerequisites without changing anything
              build               build PeerSharp CLI and the exact libtorrent checkout
              run                 build, generate fixtures, run trials, and write reports

            options:
              --libtorrent-root <path>       checkout to build (default: ../libtorrent)
              --artifacts <path>             ignored build/result root
              --engines <list>               peersharp,libtorrent
              --modes <list>                 download,upload,dual,metadata
              --variants <list>              v1,v2,hybrid
              --libtorrent-backends <list>   mmap,pread,posix
              --size-mib <n>                 payload size; piece size is 1 MiB (default 64)
              --files <n>                    files per torrent (default 4)
              --peers <n>                    simulated peer connections (default 4)
              --churn <n>                    peer reconnects every n blocks; smaller churns more (default 0, off)
              --corrupt                      the peer sends some corrupt pieces (download and dual)
              --warmups <n>                  unreported trials per case (default 1)
              --iterations <n>               measured trials per case (default 3)
              --timeout <seconds>            per-transfer timeout (default 180)
              --seed <n>                     deterministic case-order seed (default 1741)
              --skip-build                   reuse existing binaries
              --keep-run-data                preserve payload directories after a run
              -h, --help                     show this text

            Lists are comma-separated. Runs use only loopback, with DHT, LSD, UPnP and
            NAT-PMP disabled. Results are warm/uncontrolled-cache on Windows unless the
            OS cache is evicted externally between trials.
            """);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PeerSharp.slnx"))) return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PeerSharp.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find PeerSharp.slnx above the executable or working directory.");
    }

    private static bool Take(string[] args, ref int index, TextWriter error, string option, out string value)
    {
        if (++index < args.Length)
        {
            value = args[index];
            return true;
        }

        error.WriteLine($"{option} requires a value.");
        value = string.Empty;
        return false;
    }

    private static bool TakeList(
        string[] args,
        ref int index,
        TextWriter error,
        string option,
        IReadOnlyCollection<string> allowed,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!Take(args, ref index, error, option, out string raw)) return false;
        string[] parsed = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] invalid = parsed.Where(value => !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (parsed.Length == 0 || invalid.Length != 0)
        {
            error.WriteLine($"{option} must contain only: {string.Join(',', allowed)}.");
            return false;
        }

        values = parsed.Select(static value => value.ToLowerInvariant()).Distinct().ToArray();
        return true;
    }

    private static bool TakePositive(string[] args, ref int index, TextWriter error, string option, out int value)
    {
        value = 0;
        if (!Take(args, ref index, error, option, out string raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value <= 0)
        {
            error.WriteLine($"{option} requires a positive integer.");
            return false;
        }

        return true;
    }

    private static bool TakeNonNegative(string[] args, ref int index, TextWriter error, string option, out int value)
    {
        value = 0;
        if (!Take(args, ref index, error, option, out string raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value < 0)
        {
            error.WriteLine($"{option} requires a non-negative integer.");
            return false;
        }

        return true;
    }
}
