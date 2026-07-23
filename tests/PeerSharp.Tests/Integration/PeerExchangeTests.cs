using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public class PeerExchangeTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ILoggerFactory _loggerFactory;

    public PeerExchangeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MtTorrentTests_PEX_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task Peers_AreDiscovered_ViaPex()
    {
        // Scenario:
        // A <-> B <-> C
        // A should discover C via B using PEX.
        // DHT and LSD are disabled to ensure PEX is the only mechanism.

        var timeProvider = new FakeTimeProvider();

        // 1. Setup Engines
        // Use random ports for all.
        await using var engineA = await CreateEngineAsync(Path.Combine(_testRoot, "PeerA"), timeProvider);
        await using var engineB = await CreateEngineAsync(Path.Combine(_testRoot, "PeerB"), timeProvider);
        await using var engineC = await CreateEngineAsync(Path.Combine(_testRoot, "PeerC"), timeProvider);

        var torrentFile = CreateTestTorrent();

        // 2. Add Torrent
        var torrentA = await engineA.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        var torrentB = await engineB.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        var torrentC = await engineC.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await torrentA.StartAsync();
        await torrentB.StartAsync();
        await torrentC.StartAsync();

        // 3. Connect A <-> B
        // We explicitly add B as a peer to A.
        var portB = engineB.Settings.Connection.TcpPort;
        var epB = new IPEndPoint(IPAddress.Loopback, portB);
        engineA.OnPeersFound(torrentA.Hash, [epB]);

        // Wait for A and B to connect
        await WaitForConditionAsync(() => torrentA.Peers.ConnectedCount > 0 && torrentB.Peers.ConnectedCount > 0, TimeSpan.FromSeconds(5), "A <-> B connection");

        // 4. Connect B <-> C
        // We explicitly add C as a peer to B.
        var portC = engineC.Settings.Connection.TcpPort;
        var epC = new IPEndPoint(IPAddress.Loopback, portC);
        engineB.OnPeersFound(torrentB.Hash, [epC]);

        // Wait for B and C to connect
        await WaitForConditionAsync(() => torrentB.Peers.ConnectedCount >= 2 && torrentC.Peers.ConnectedCount > 0, TimeSpan.FromSeconds(5), "B <-> C connection");

        Assert.Equal(1, torrentA.Peers.ConnectedCount); // A knows only B
        Assert.Equal(2, torrentB.Peers.ConnectedCount); // B knows A and C
        Assert.Equal(1, torrentC.Peers.ConnectedCount); // C knows only B

        // 5. Advance Time to Trigger PEX
        // PEX interval is 60 seconds (PeerManager.PexIntervalSeconds).
        // B should send PEX to A containing C.
        // B should send PEX to C containing A.

        // We need to advance time sufficiently. The PeerManager ticks periodically (every 1s).
        // PEX broadcast happens every 60 ticks.
        // We must advance time in small steps to let the loop in PeerManager execute each tick.
        for (int i = 0; i < 70; i++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            // Small real delay to allow the background task to resume and process the tick
            await Task.Delay(5);
        }

        // 6. Verify Discovery
        // A should have connected to C.
        // C should have connected to A.
        // Note: Due to loopback testing and PEX behavior, peers might also connect to themselves or have duplicate attempts.
        // We ensure at least 2 peers (B + C for A, B + A for C).
        await WaitForConditionAsync(() =>
            torrentA.Peers.ConnectedCount >= 2 && torrentC.Peers.ConnectedCount >= 2,
            TimeSpan.FromSeconds(10),
            "PEX Discovery (A <-> C)");

        Assert.True(torrentA.Peers.ConnectedCount >= 2, $"A should have at least 2 peers (B and C). Actual: {torrentA.Peers.ConnectedCount}");
        Assert.True(torrentC.Peers.ConnectedCount >= 2, $"C should have at least 2 peers (B and A). Actual: {torrentC.Peers.ConnectedCount}");
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, TimeProvider timeProvider)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0, // Random port
                UdpPort = 0,
                EnableLsd = false, // Disable LSD
                EnableTcpIn = true,
                EnableUtpIn = false,
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = false } // Disable DHT
        };

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
        const string fileName = "pex_test.bin";
        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        return new PeerSharp.Core.TorrentFileBuilder()
             .WithName("PexTest")
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
            // Fall through to assertion failure in caller or throw descriptive
            throw new Exception($"Timed out waiting for {description}");
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





