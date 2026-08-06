using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Peer exchange, end to end, over real connections.
///
/// <para>
/// The unit tests cover which peers the broadcaster picks and when. What they cannot show is that a
/// message is built, encoded, sent, received, decoded and acted on - every layer between the policy
/// and another engine's peer list. This runs three engines to prove exactly that, and it is arranged
/// so PEX is the <em>only</em> way the result can happen: two leechers are each told about the seeder
/// and never about each other, so if they end up connected, the seeder told them.
/// </para>
///
/// <para>
/// Loopback is too fast for the default minute-long interval to be reachable in a test - a 192 MiB
/// transfer across 24 peers finishes in under eight seconds - so this leans on
/// <c>ConnectionSettings.PexInterval</c> being adjustable.
/// </para>
/// </summary>
[Collection("LiveEngine")]
public sealed class PexLiveExchangeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public PexLiveExchangeTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "peersharp-pex-" + Guid.NewGuid().ToString("N")[..8]);
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
            // A peer may still be closing its files; the directory is under temp and per-run.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoLeechersLearnAboutEachOther_OnlyViaTheSeeder()
    {
        var interval = TimeSpan.FromSeconds(2);

        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);

        const string fileName = "pex-payload.bin";
        var payload = new byte[2 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var seedFile = Path.Combine(seedDir, fileName);
        await File.WriteAllBytesAsync(seedFile, payload);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(256 * 1024)
            .AddFileFromPath(seedFile, fileName)
            .Build();

        await using var seed = await CreateEngineAsync(seedDir, interval);
        var seedTorrent = await seed.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seedTorrent.ForceRecheckAsync());
        await seedTorrent.StartAsync();

        var seedListener = seed.PortListener ?? throw new InvalidOperationException("Seeder is not listening.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, seedListener.Port);

        var leechDirA = Path.Combine(_root, "leech-a");
        var leechDirB = Path.Combine(_root, "leech-b");
        Directory.CreateDirectory(leechDirA);
        Directory.CreateDirectory(leechDirB);

        await using var leechA = await CreateEngineAsync(leechDirA, interval);
        await using var leechB = await CreateEngineAsync(leechDirB, interval);

        var torrentA = await leechA.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
        var torrentB = await leechB.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        // Rate limited so both are still leeching when peer exchange delivers, which is the situation
        // this test is about. Unthrottled, two megabytes over loopback finish inside the first PEX
        // interval, and two peers that have everything have no reason to connect to each other - the
        // engine now declines that dial outright, so an unthrottled run would be asserting that a
        // connection nobody wants gets made. At this rate the transfer spans several intervals.
        torrentA.DownloadLimitBytesPerSecond = 128 * 1024;
        torrentB.DownloadLimitBytesPerSecond = 128 * 1024;

        // The only address either leecher is ever given. Neither is told the other exists.
        leechA.OnPeersFound(torrentFile.InfoHash, [seedEndpoint]);
        leechB.OnPeersFound(torrentFile.InfoHash, [seedEndpoint]);

        var listenerA = leechA.PortListener ?? throw new InvalidOperationException("Leecher A is not listening.");
        var listenerB = leechB.PortListener ?? throw new InvalidOperationException("Leecher B is not listening.");
        _output.WriteLine($"seed={seedEndpoint.Port} leechA={listenerA.Port} leechB={listenerB.Port}");

        // Each leecher starts knowing one peer: the seeder. If either ends up with two, the second can
        // only have come from a PEX message, because nothing else in this test carries that address.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (torrentA.Peers.ConnectedCount >= 2 || torrentB.Peers.ConnectedCount >= 2)
            {
                _output.WriteLine(
                    $"leechA peers={torrentA.Peers.ConnectedCount}, leechB peers={torrentB.Peers.ConnectedCount}");
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail(
            "Neither leecher ever connected to a second peer, so no usable PEX message arrived. " +
            $"leechA={torrentA.Peers.ConnectedCount}, leechB={torrentB.Peers.ConnectedCount}, " +
            $"seeder={seedTorrent.Peers.ConnectedCount}.");
    }

    private static async Task<ClientEngine> CreateEngineAsync(string dir, TimeSpan pexInterval)
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
                Encryption = Encryption.Allow,

                // A minute is unreachable on loopback: the transfer is over long before it elapses.
                PexInterval = pexInterval
            },

            // Both would give a leecher another route to its sibling, which would make a pass here
            // prove nothing about peer exchange.
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
}
