using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Net;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Encrypted peers connecting to us over uTP.
///
/// <para>
/// Encryption and transport are independent choices. Both reference implementations treat them that
/// way - libtorrent decides encryption from its policy setting and the peer's advertised support, with
/// no reference to the socket type, and Transmission hands every inbound connection to the same
/// <c>tr_handshake</c> with <c>session-&gt;encryptionMode()</c> whether it arrived over TCP or uTP.
/// Encrypted uTP is not a corner case either: in a live swarm measurement 63 of 68 qBittorrent
/// connections were uTP and all 68 were encrypted.
/// </para>
///
/// <para>
/// Our inbound uTP path used to read 68 bytes and require the first to be 19, so an encrypted peer -
/// whose first bytes are a Diffie-Hellman key, indistinguishable from noise - was rejected outright.
/// Outbound was always fine, because it runs the same handshake regardless of transport, which is why
/// this only ever showed up as unexplained "Invalid uTP handshake" warnings.
/// </para>
/// </summary>
[Collection("Integration")]
public class EncryptedUtpTests : IDisposable
{
    // Generous because this binds real sockets and runs alongside the rest of the suite. The point is
    // whether an encrypted uTP peer can connect and transfer at all, not how quickly it does so on a
    // loaded machine.
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(120);

    private readonly string _testRoot;
    private readonly string _seedPath;
    private readonly string _leechPath;
    private readonly ILoggerFactory _loggerFactory;

    public EncryptedUtpTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpUtpMse_" + Guid.NewGuid().ToString("N"));
        _seedPath = Path.Combine(_testRoot, "seed");
        _leechPath = Path.Combine(_testRoot, "leech");
        Directory.CreateDirectory(_seedPath);
        Directory.CreateDirectory(_leechPath);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Theory(Timeout = 180000)]
    [InlineData(Encryption.Require)]
    [InlineData(Encryption.Allow)]
    public async Task EncryptedPeerConnectingOverUtp_CanDownload(Encryption encryption)
    {
        const string fileName = "utp-mse.bin";
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_seedPath, fileName), payload, TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(64 * 1024)
            .AddFile(fileName, payload)
            .Build();

        // TCP is disabled on both sides, so the only way through is uTP. With Require, the only way
        // through is uTP carrying MSE - exactly the combination the inbound path used to refuse.
        await using var seedEngine = await CreateEngineAsync(_seedPath, encryption);
        var seed = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seed.ForceRecheckAsync());
        await seed.StartAsync();

        await using var leechEngine = await CreateEngineAsync(_leechPath, encryption);
        var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        // uTP rides on UDP, so the seed must be dialled on its bound UDP port rather than its TCP one.
        // With both configured as 0 they land on different ephemeral ports.
        Assert.True(seedEngine.BoundUdpPort > 0, "The seed engine did not bind a UDP port, so uTP cannot be reached.");
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, seedEngine.BoundUdpPort);

        var clock = Stopwatch.StartNew();
        while (leech.Peers.ConnectedCount == 0 && clock.Elapsed < ConnectionTimeout)
        {
            leechEngine.OnPeersFound(leech.Hash, [seedEndpoint]);
            await Task.Delay(250);
        }

        Assert.True(
            leech.Peers.ConnectedCount > 0,
            $"No uTP connection was established with {encryption} encryption. An encrypted peer's first bytes are " +
            "a Diffie-Hellman key; if the inbound uTP path assumes plaintext it rejects them as a malformed " +
            "handshake.");

        var peer = leech.Peers.GetConnectedPeers()[0];
        Assert.True(peer.IsUtp, "The connection did not use uTP, so this run did not exercise the inbound uTP path.");

        if (encryption == Encryption.Require)
        {
            Assert.True(peer.IsEncrypted, "Encryption was required but the connection was not encrypted.");
        }

        clock.Restart();
        while (!leech.Finished && clock.Elapsed < TransferTimeout)
        {
            await Task.Delay(200);
        }

        Assert.True(leech.Finished, $"The download did not complete over encrypted uTP within {TransferTimeout}.");
        await leech.StopAsync();

        byte[] downloaded = await File.ReadAllBytesAsync(
            Path.Combine(_leechPath, leech.GetFileInfo(0).Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(payload),
            System.Security.Cryptography.SHA256.HashData(downloaded));
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, Encryption encryption)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,

                // uTP only, so nothing can quietly fall back to TCP and hide the defect.
                EnableTcpIn = false,
                EnableTcpOut = false,
                EnableUtpIn = true,
                EnableUtpOut = true,
                PreferUtp = true,

                // uTP is held back for a warmup period unless a peer is known to support it. With TCP
                // disabled that leaves no transport at all, so the dial never happens.
                UtpWarmupSeconds = 0,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = encryption
            },
            Dht = { Enabled = false }
        };

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync();
        return engine;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
