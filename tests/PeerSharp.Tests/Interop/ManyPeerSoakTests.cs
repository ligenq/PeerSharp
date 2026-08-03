using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// One seeder, many simultaneous leechers.
///
/// <para>
/// Every other measurement in this namespace is a single connection moving as fast as loopback
/// allows, which says nothing about the cost that actually scales. What scales is per-peer state: the
/// upload request queue is bounded per connection, so its memory cost is multiplied by however many
/// peers are attached. <see cref="ProtocolConstants.MaxOutstandingRequestsPerPeer"/> is the number
/// that decision turns on, and raising it was proposed to lift the seeding ceiling against clients
/// that only refill their request window on a timer.
/// </para>
///
/// <para>
/// This exists to make that a measurement rather than a guess. It reports aggregate throughput and the
/// managed heap high-water mark against peer count, so the memory cost per attached peer can be read
/// off directly and multiplied out for a larger depth. To compare depths, change the constant and run
/// it again - it is deliberately a reported measurement rather than a threshold assertion, because the
/// right ceiling depends on the deployment rather than on this machine.
/// </para>
///
/// <para>
/// Opt-in like the rest of this namespace: set <c>PEERSHARP_SOAK=1</c>. Peer count and payload size
/// come from <c>PEERSHARP_SOAK_PEERS</c> and <c>PEERSHARP_SOAK_SIZE_MIB</c>.
/// </para>
/// </summary>
public sealed class ManyPeerSoakTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public ManyPeerSoakTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "peersharp-soak-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leecher may still be closing its files. The directory is under the temp root and the
            // next run uses a fresh one, so leaving it is harmless.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ManyLeechers_AllComplete_AndTheirCostIsReported()
    {
        if (Environment.GetEnvironmentVariable("PEERSHARP_SOAK") != "1")
        {
            Assert.Skip("Set PEERSHARP_SOAK=1 to run the many-peer soak.");
        }

        int peerCount = IntFromEnvironment("PEERSHARP_SOAK_PEERS", 24);
        int sizeMiB = IntFromEnvironment("PEERSHARP_SOAK_SIZE_MIB", 16);
        var timeout = TimeSpan.FromMinutes(IntFromEnvironment("PEERSHARP_SOAK_TIMEOUT_MINUTES", 10));

        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);

        const string fileName = "soak-payload.bin";
        var payload = new byte[sizeMiB * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var seedFile = Path.Combine(seedDir, fileName);
        await File.WriteAllBytesAsync(seedFile, payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(seedFile, fileName)
            .Build();

        _output.WriteLine($"Peers        : {peerCount}");
        _output.WriteLine($"Payload      : {sizeMiB} MiB, {torrentFile.PieceCount} pieces");
        _output.WriteLine($"Request depth: {ProtocolConstants.MaxOutstandingRequestsPerPeer} per peer " +
            $"({ProtocolConstants.MaxOutstandingRequestsPerPeer * (long)ProtocolConstants.BlockSize / 1024 / 1024} MiB " +
            "of blocks in flight per peer at worst)");

        await using var seedEngine = await CreateEngineAsync(seedDir);
        var seedTorrent = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seedTorrent.ForceRecheckAsync());
        await seedTorrent.StartAsync();

        var listener = seedEngine.PortListener ?? throw new InvalidOperationException("Seed engine has no port listener.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);

        // Settle before the baseline so the seeder's own buffers are not counted as peer cost.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        var engines = new List<ClientEngine>(peerCount);
        var torrents = new List<PeerSharp.Interfaces.ITorrent>(peerCount);
        var leechDirs = new List<string>(peerCount);

        try
        {
            for (int i = 0; i < peerCount; i++)
            {
                var dir = Path.Combine(_root, "leech-" + i);
                Directory.CreateDirectory(dir);
                leechDirs.Add(dir);

                var engine = await CreateEngineAsync(dir);
                engines.Add(engine);
                torrents.Add(await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true }));
            }

            var clock = Stopwatch.StartNew();
            long peakBytes = baselineBytes;
            var lastLog = TimeSpan.Zero;

            while (clock.Elapsed < timeout && torrents.Any(t => t.Progress < 1.0f))
            {
                foreach (var engine in engines)
                {
                    engine.OnPeersFound(torrentFile.InfoHash, [seedEndpoint]);
                }

                peakBytes = Math.Max(peakBytes, GC.GetTotalMemory(forceFullCollection: false));

                if (clock.Elapsed - lastLog > TimeSpan.FromSeconds(5))
                {
                    lastLog = clock.Elapsed;
                    int done = torrents.Count(t => t.Progress >= 1.0f);
                    _output.WriteLine(
                        $"  [{clock.Elapsed.TotalSeconds,5:F1}s] complete={done}/{peerCount} " +
                        $"seederPeers={seedTorrent.Peers.ConnectedCount} " +
                        $"heap={peakBytes / 1024 / 1024} MiB");
                }

                await Task.Delay(250);
            }

            clock.Stop();

            int completed = torrents.Count(t => t.Progress >= 1.0f);
            double totalMib = completed * (payload.Length / 1024d / 1024d);
            long perPeerKib = (peakBytes - baselineBytes) / Math.Max(peerCount, 1) / 1024;

            _output.WriteLine(string.Empty);
            _output.WriteLine($"Completed    : {completed}/{peerCount} in {clock.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"Aggregate    : {totalMib / clock.Elapsed.TotalSeconds:F1} MiB/s across all peers");
            _output.WriteLine($"Heap baseline: {baselineBytes / 1024 / 1024} MiB");
            _output.WriteLine($"Heap peak    : {peakBytes / 1024 / 1024} MiB");
            _output.WriteLine($"Cost per peer: ~{perPeerKib} KiB of managed heap");

            // The heap figure above is whole-process and dominated by the leechers buffering their own
            // copies of the payload, which is an artefact of running them all here rather than anything
            // a real seeder pays. What the request depth itself costs is separable and small: the queue
            // is a bounded channel of 12-byte descriptors, and block data is read lazily one item at a
            // time as each is served, so depth buys descriptors rather than buffers. In-flight block
            // data is bounded by the send queue instead, independently of this number.
            long queueBytesPerPeer = ProtocolConstants.MaxOutstandingRequestsPerPeer * 12L;
            _output.WriteLine(
                $"Request queue: {queueBytesPerPeer / 1024} KiB per peer of descriptors " +
                $"({queueBytesPerPeer * 200 / 1024 / 1024} MiB at 200 peers) - the part depth controls");

            Assert.True(
                completed == peerCount,
                $"Only {completed} of {peerCount} leechers finished within {clock.Elapsed.TotalSeconds:F0}s.");

            // Spot-check rather than every copy: hashing peerCount payloads costs more than it proves.
            // Shared read because the leecher still holds the file open until its engine is disposed.
            var sample = Path.Combine(leechDirs[0], fileName);
            await using var stream = new FileStream(
                sample, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(expectedHash, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        }
        finally
        {
            foreach (var engine in engines)
            {
                await engine.DisposeAsync();
            }
        }
    }

    private static async Task<ClientEngine> CreateEngineAsync(string dir)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = dir },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Allow
            },
            Dht = { Enabled = false },
            Session = { Enabled = false }
        };

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            Settings = settings
        });

        await engine.InitializeAsync();
        return engine;
    }

    private static int IntFromEnvironment(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
    }
}
