using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Integration;

public class PeerDiscoveryTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ILoggerFactory _loggerFactory;

    public PeerDiscoveryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MtTorrentTests_Discovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task Lsd_FindsLocalPeer_WhenEnabled()
    {
        var timeProvider = new FakeTimeProvider();

        // Setup two engines with LSD enabled.
        await using var engineA = await CreateEngineAsync(Path.Combine(_testRoot, "LsdA"), timeProvider, enableLsd: true, enableDht: false);
        await using var engineB = await CreateEngineAsync(Path.Combine(_testRoot, "LsdB"), timeProvider, enableLsd: true, enableDht: false);

        var torrentFile = CreateTestTorrent();

        var torrentA = await engineA.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        var torrentB = await engineB.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await torrentA.StartAsync();
        await torrentB.StartAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        await WaitForConditionAsync(() =>
            torrentA.Peers.ConnectedCount > 0 || torrentB.Peers.ConnectedCount > 0,
            TimeSpan.FromSeconds(5),
            "LSD Discovery");

        Assert.True(torrentA.Peers.ConnectedCount > 0 || torrentB.Peers.ConnectedCount > 0);
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, TimeProvider timeProvider, bool enableLsd = true, bool enableDht = false, int bootstrapPort = 0)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0, // Random port
                UdpPort = 0,
                EnableLsd = enableLsd,
                EnableUtpIn = false, // Disable uTP to simplify (focus on TCP connection via DHT discovery)
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = enableDht }
        };

        if (enableDht)
        {
            settings.Dht.BootstrapNodes = bootstrapPort > 0
                ? new List<DhtBootstrapNode> { new("127.0.0.1", (ushort)bootstrapPort) }
                : Array.Empty<DhtBootstrapNode>();
        }

        var engine = ClientEngine.Create(
            settings,
            null, // bandwidth
            null, // alerts
            null, // networkManager
            true, // takeOwnership
            timeProvider,
            _loggerFactory
        );

        await engine.InitializeAsync();
        return engine;
    }

    private static TorrentFile CreateTestTorrent()
    {
        const string fileName = "discovery.bin";
        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        return new PeerSharp.Core.TorrentFileBuilder()
             .WithName("DiscoveryTest")
             .WithPieceLength(16384)
             .AddFile(fileName, data)
             .Build();
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!condition())
            {
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new Exception($"Timed out waiting for {description}");
        }
    }

    private sealed class InMemoryUdpNetwork
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<int, InMemoryUdpListener> _listeners = [];
        private int _nextPort = 10000;
        private int _inFlight;

        public int BootstrapPort { get; set; }

        public InMemoryUdpListener CreateListener()
        {
            lock (_lock)
            {
                int port = _nextPort++;
                var listener = new InMemoryUdpListener(this, port);
                _listeners[port] = listener;
                return listener;
            }
        }

        public bool TryGetListener(int port, out InMemoryUdpListener listener)
        {
            lock (_lock)
            {
                return _listeners.TryGetValue(port, out listener!);
            }
        }

        public bool ShouldDeliver(int fromPort, int toPort)
        {
            if (BootstrapPort == 0)
            {
                return true;
            }

            if (fromPort == BootstrapPort || toPort == BootstrapPort)
            {
                return true;
            }

            return false;
        }

        public void Track(Task task)
        {
            Interlocked.Increment(ref _inFlight);
            _ = task.ContinueWith(_ => Interlocked.Decrement(ref _inFlight), TaskScheduler.Default);
        }

        public async Task DrainAsync(TimeSpan timeout)
        {
            var start = DateTimeOffset.UtcNow;
            while (Interlocked.CompareExchange(ref _inFlight, 0, 0) != 0)
            {
                if (DateTimeOffset.UtcNow - start > timeout)
                {
                    return;
                }

                await Task.Delay(5);
            }
        }

        public void RemoveListener(int port)
        {
            lock (_lock)
            {
                _listeners.Remove(port);
            }
        }
    }

    private sealed class InMemoryUdpListener : IUdpListener
    {
        private readonly InMemoryUdpNetwork _network;
        private IUdpReceiver? _receiver;

        public InMemoryUdpListener(InMemoryUdpNetwork network, int port)
        {
            _network = network;
            Port = port;
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port);
        }

        public int Port { get; }
        public IPEndPoint LocalEndPoint { get; }

        public void RegisterReceiver(IUdpReceiver receiver)
        {
            _receiver = receiver;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
        {
            if (!_network.TryGetListener(endpoint.Port, out var target))
            {
                return Task.CompletedTask;
            }

            if (!_network.ShouldDeliver(Port, endpoint.Port))
            {
                return Task.CompletedTask;
            }

            var payload = data.ToArray();
            var task = Task.Run(() => target._receiver?.Receive(payload, LocalEndPoint), CancellationToken.None);
            _network.Track(task);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _network.RemoveListener(Port);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDhtCallback : IDhtCallback
    {
        private readonly List<(InfoHash Hash, List<IPEndPoint> Peers)> _found = [];

        public void OnPeersFound(InfoHash infoHash, List<IPEndPoint> peers)
        {
            _found.Add((infoHash, peers));
        }

        public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers) { }

        public bool ContainsPeer(InfoHash hash, IPEndPoint peer)
        {
            foreach (var entry in _found)
            {
                if (entry.Hash != hash)
                {
                    continue;
                }

                if (entry.Peers.Any(p => p.Equals(peer)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }
}





