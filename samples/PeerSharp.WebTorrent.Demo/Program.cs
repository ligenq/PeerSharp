using Microsoft.Extensions.Logging;
using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;
using PeerSharp.Interfaces;
using PeerSharp.WebTorrent;
using PeerSharp.WebTorrent.Configuration;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        args = args.Length == 0 ? new[] { @"C:\Users\jlign\Downloads\sintel (1).torrent" } : args;

        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        var options = DemoOptions.Parse(args);
        if (options.ShowHelp || string.IsNullOrWhiteSpace(options.TorrentPath))
        {
            PrintUsage();
            return options.ShowHelp ? 0 : 1;
        }

        string torrentPath = options.TorrentPath;
        string downloadPath = options.DownloadPath ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");

        if (options.IceTransportPolicy == WebTorrentIceTransportPolicy.Relay
            && !options.IceServers.Any(server => server.Urls.Any(url => url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("--ice-policy relay requires at least one --turn or --turns server.");
            Console.WriteLine();
            PrintUsage();
            return 1;
        }

        if (!File.Exists(torrentPath))
        {
            Console.Error.WriteLine($"Torrent file not found: {torrentPath}");
            return 1;
        }

        // Set up logging
        const string LogFilePath = @"C:\repos\PeerSharp\peersharp-demo.log";
        using var fileLoggerProvider = new FileLoggerProvider(LogFilePath);
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddProvider(fileLoggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("PeerSharp.WebTorrent", LogLevel.Debug);
            builder.AddFilter("RtcForge", LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger("WebTorrentDemo");

        // Configure engine for WebTorrent-only: disable TCP, uTP, and DHT
        var settings = new Settings
        {
            Connection = new ConnectionSettings
            {
                EnableTcpIn = false,
                EnableTcpOut = false,
                EnableUtpIn = false,
                EnableUtpOut = false,
                EnableLsd = false,
                EnableWebSeeds = false
            },
            Dht = new DhtSettings { Enabled = false },
            Files = new FilesSettings { DefaultDownloadPath = downloadPath }
        };

        var iceServers = options.IceServers.Count > 0
            ? options.IceServers
            :
            [
                new() { Urls = { "stun:stun.l.google.com:19302" } },
                new() { Urls = { "stun:stun1.l.google.com:19302" } }
            ];

        var webTorrentOptions = new WebTorrentSessionOptions
        {
            OffersPerTracker = 5,
            IceTransportPolicy = options.IceTransportPolicy,
            IceServers = iceServers,
            AdditionalTrackers = new[]
            {
                "wss://tracker.openwebtorrent.com",
                "wss://tracker.openwebtorrent.com:443/announce",
                "wss://tracker.webtorrent.dev",
                "wss://tracker.webtorrent.dev:443/announce",
                "ws://tracker.files.fm:7072/announce"
            }
        };

        var engineOptions = new TorrentClientOptions
        {
            Settings = settings,
            LoggerFactory = loggerFactory
        };

        var engine = ClientEngineFactory.Create(engineOptions);
        await engine.InitializeAsync();

        logger.LogInformation("Engine initialized (WebTorrent-only mode)");
        logger.LogInformation("Download path: {Path}", downloadPath);
        logger.LogInformation("ICE transport policy: {Policy}", webTorrentOptions.IceTransportPolicy);
        foreach (var iceServer in webTorrentOptions.IceServers)
        {
            logger.LogInformation(
                "  ICE server: {Urls}{Auth}",
                string.Join(", ", iceServer.Urls),
                string.IsNullOrEmpty(iceServer.Username) ? "" : " (auth configured)");
        }

        bool hasTurn = webTorrentOptions.IceServers.Any(server => server.Urls.Any(url =>
            url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)));
        if (!hasTurn)
        {
            logger.LogWarning("No TURN server configured. Most browser WebTorrent peers sit behind symmetric NAT and require a TURN relay to connect; expect ICE pair-checks to time out for the majority of peers. Pass --turn <url> [--turn-user X --turn-pass Y] to enable relayed connectivity.");
        }

        // Load torrent file
        var torrentFile = TorrentFile.Load(torrentPath);
        logger.LogInformation("Loaded torrent: {Name}", torrentFile.Name);
        foreach (var webSeed in torrentFile.WebSeeds)
        {
            logger.LogInformation("  Web seed: {Url}", webSeed);
        }

        // Set up events
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var events = new TorrentEventsBuilder()
            .OnStateChanged((t, s) => logger.LogInformation("State: {Previous} -> {New}", s.PreviousState, s.NewState))
            .OnProgressChanged((t, p) =>
            {
                logger.LogInformation("Progress: {Progress:P1} ({Completed}/{Total} pieces, {Remaining} bytes remaining)",
                    p.Progress, p.CompletedPieces, p.TotalPieces, p.RemainingBytes);
            })
            .OnTransferStats((t, s) =>
            {
                logger.LogInformation("Peers: {Peers}, DL: {DL} B/s, UL: {UL} B/s",
                    s.ConnectedPeers, s.DownloadSpeed, s.UploadSpeed);
            })
            .OnPieceCompleted((t, p) =>
            {
                logger.LogInformation("Piece {Index} completed ({Completed}/{Total})",
                    p.PieceIndex, p.CompletedPieces, p.TotalPieces);
            })
            .OnFinished((t, selectedOnly) =>
            {
                logger.LogInformation("Download complete!");
                finished.TrySetResult();
            })
            .OnError((t, ex) => logger.LogError(ex, "Torrent error"))
            .Build();

        // Add torrent
        var addOptions = new AddTorrentOptions(downloadPath)
        {
            StartImmediately = false,
            Events = events
        };

        var torrent = await engine.AddTorrentAsync(torrentFile, addOptions);
        logger.LogInformation("Torrent added: {Hash} ({Size} bytes, {Pieces} pieces)",
            torrent.Hash, torrent.TotalSize, torrent.PieceCount);

        // List trackers
        var trackers = torrent.Trackers.GetTrackers();
        int wsTrackerCount = 0;
        foreach (var tracker in trackers)
        {
            bool isWs = tracker.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                     || tracker.Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
            if (isWs)
            {
                wsTrackerCount++;
            }

            logger.LogInformation("  Tracker: {Url}{Tag}", tracker.Url, isWs ? " (WebSocket)" : "");
        }

        foreach (var tracker in trackers.Where(static tracker =>
            !tracker.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            && !tracker.Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
        {
            if (torrent.Trackers.RemoveTracker(tracker.Url))
            {
                logger.LogInformation("  Removed classic tracker for WebTorrent-only mode: {Url}", tracker.Url);
            }
        }

        if (torrentFile.WebSeeds.Count > 0)
        {
            logger.LogWarning("This torrent has web seeds; piece progress can come from HTTP web seeds even if WebRTC has not connected.");
        }

        if (wsTrackerCount == 0)
        {
            logger.LogWarning("No WebSocket trackers found in torrent. WebTorrent requires wss:// or ws:// trackers.");
            logger.LogWarning("Adding default public WebTorrent trackers...");
        }

        torrent.UseWebTorrent(webTorrentOptions, loggerFactory);

        await torrent.StartAsync();
        logger.LogInformation("WebTorrent enabled ({Offers} offers per tracker)", webTorrentOptions.OffersPerTracker);

        // Handle Ctrl+C
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Shutting down...");
            cts.Cancel();
            finished.TrySetResult();
        };

        // Wait for completion or cancellation
        try
        {
            await finished.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        logger.LogInformation("Final progress: {Progress:P1}", torrent.Progress);

        await engine.DisposeAsync();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: PeerSharp.WebTorrent.Demo <torrent-file> [download-path] [options]");
        Console.WriteLine();
        Console.WriteLine("Downloads a torrent using only WebTorrent (WebRTC) peers.");
        Console.WriteLine("The torrent must have at least one wss:// or ws:// tracker.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --turn <url>             Add a TURN server, e.g. turn:turn.example.com:3478");
        Console.WriteLine("  --turns <url>            Add a secure TURN server, e.g. turns:turn.example.com:5349");
        Console.WriteLine("  --turn-user <username>   TURN username for subsequently added TURN servers");
        Console.WriteLine("  --turn-pass <password>   TURN credential for subsequently added TURN servers");
        Console.WriteLine("  --stun <url>             Add a STUN server and replace default ICE servers");
        Console.WriteLine("  --ice-policy <all|relay> Use relay to force TURN-only connectivity testing");
    }
}

internal sealed class DemoOptions
{
    public string? TorrentPath { get; private set; }
    public string? DownloadPath { get; private set; }
    public bool ShowHelp { get; private set; }
    public WebTorrentIceTransportPolicy IceTransportPolicy { get; private set; } = WebTorrentIceTransportPolicy.All;
    public List<WebTorrentIceServer> IceServers { get; } = [];

    public static DemoOptions Parse(string[] args)
    {
        var options = new DemoOptions();
        string? turnUser = null;
        string? turnPass = null;
        bool customIceServers = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                return options;
            }

            if (arg == "--turn-user")
            {
                turnUser = ReadValue(args, ref i, arg);
                continue;
            }

            if (arg == "--turn-pass")
            {
                turnPass = ReadValue(args, ref i, arg);
                continue;
            }

            if (arg == "--turn" || arg == "--turns")
            {
                customIceServers = true;
                string url = ReadValue(args, ref i, arg);
                options.IceServers.Add(new WebTorrentIceServer
                {
                    Urls = { url },
                    Username = turnUser,
                    Credential = turnPass
                });
                continue;
            }

            if (arg == "--stun")
            {
                customIceServers = true;
                options.IceServers.Add(new WebTorrentIceServer { Urls = { ReadValue(args, ref i, arg) } });
                continue;
            }

            if (arg == "--ice-policy")
            {
                string value = ReadValue(args, ref i, arg);
                options.IceTransportPolicy = value.Equals("relay", StringComparison.OrdinalIgnoreCase)
                    ? WebTorrentIceTransportPolicy.Relay
                    : WebTorrentIceTransportPolicy.All;
                continue;
            }

            if (options.TorrentPath == null)
            {
                options.TorrentPath = arg;
            }
            else if (options.DownloadPath == null)
            {
                options.DownloadPath = arg;
            }
        }

        if (!customIceServers)
        {
            options.IceServers.Clear();
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[++index];
    }
}

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock, () => _disposed);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class FileLogger(string category, StreamWriter writer, object gate, Func<bool> isDisposed) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} {LevelTag(logLevel)} {category}: {message}";
            lock (gate)
            {
                if (isDisposed())
                {
                    return;
                }

                writer.WriteLine(line);
                if (exception != null)
                {
                    writer.WriteLine(exception);
                }
            }
        }

        private static string LevelTag(LogLevel l) => l switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
    }
}
