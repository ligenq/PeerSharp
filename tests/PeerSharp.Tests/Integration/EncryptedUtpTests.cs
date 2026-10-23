using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Net;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// MSE peers using encrypted or plaintext payloads over TCP and uTP.
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
    [InlineData(Encryption.Require, false, true)]
    [InlineData(Encryption.Allow, false, true)]
    [InlineData(Encryption.Allow, false, false)]
    [InlineData(Encryption.Require, false, false)]
    [InlineData(Encryption.Allow, true, false)]
    [InlineData(Encryption.Require, true, false)]
    [InlineData(Encryption.Allow, true, true)]
    public Task MsePayloadModes_CanDownload(Encryption encryption, bool tcp, bool preferRc4)
        => DownloadAsync(encryption, tcp, preferRc4, directionalOverrides: false);

    [Fact(Timeout = 180000)]
    public Task DirectionalOverridesAreAppliedToBothIncomingAndOutgoingHandshakes()
        => DownloadAsync(Encryption.Refuse, tcp: true, preferRc4: true, directionalOverrides: true);

    private async Task DownloadAsync(Encryption encryption, bool tcp, bool preferRc4, bool directionalOverrides)
    {
        const string fileName = "utp-mse.bin";
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_seedPath, fileName), payload, cancellationToken: TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(64 * 1024)
            .AddFile(fileName, payload)
            .Build();

        // Enable only the selected transport. Require must keep payloads encrypted even when the
        // preference is plaintext; Allow exercises both selections after MSE.
        await using var seedEngine = await CreateEngineAsync(_seedPath, encryption, tcp, preferRc4, directionalOverrides);
        var seed = await seedEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await seed.ForceRecheckAsync());
        await seed.StartAsync();

        await using var leechEngine = await CreateEngineAsync(_leechPath, encryption, tcp, preferRc4, directionalOverrides);
        var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });

        // The listener ports are ephemeral, and uTP uses the bound UDP port.
        int port = tcp ? seedEngine.BoundTcpPort : seedEngine.BoundUdpPort;
        Assert.True(port > 0);
        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, port);

        var clock = Stopwatch.StartNew();
        while (leech.Peers.ConnectedCount == 0 && clock.Elapsed < ConnectionTimeout)
        {
            leechEngine.OnPeersFound(leech.Hash, [seedEndpoint]);
            await Task.Delay(250);
        }

        Assert.True(
            leech.Peers.ConnectedCount > 0,
            $"No {(tcp ? "TCP" : "uTP")} connection was established with {encryption} policy and PreferRc4={preferRc4}.");

        var peer = leech.Peers.GetConnectedPeers()[0];
        Assert.Equal(!tcp, peer.IsUtp);
        Assert.Equal(encryption != Encryption.Refuse && (encryption == Encryption.Require || preferRc4), peer.IsEncrypted);
        var internalTorrent = Assert.IsType<Torrent>(leech);
        Assert.Contains(internalTorrent.PeersInternal.GetConnectedPeersInternal(), connection => connection.IsOutgoing && connection.UsedMseHandshake == (encryption != Encryption.Refuse));

        clock.Restart();
        while (!leech.Finished && clock.Elapsed < TransferTimeout)
        {
            await Task.Delay(200);
        }

        Assert.True(leech.Finished, $"The download did not complete over MSE within {TransferTimeout}.");
        if (encryption != Encryption.Refuse)
            Assert.DoesNotContain(internalTorrent.PeersInternal.ExportConnectionPreferences(), preference => !preference.OfferEncryptionNext);
        await leech.StopAsync();

        byte[] downloaded = await File.ReadAllBytesAsync(
            Path.Combine(_leechPath, leech.GetFileInfo(0).Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(payload),
            System.Security.Cryptography.SHA256.HashData(downloaded));
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, Encryption encryption, bool tcp, bool preferRc4, bool directionalOverrides)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,

                // Enable only the transport the case is exercising.
                EnableTcpIn = tcp,
                EnableTcpOut = tcp,
                EnableUtpIn = !tcp,
                EnableUtpOut = !tcp,
                PreferUtp = !tcp,

                // uTP is held back for a warmup period unless a peer is known to support it. With TCP
                // disabled that leaves no transport at all, so the dial never happens.
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = directionalOverrides ? Encryption.Require : encryption,
                IncomingEncryption = directionalOverrides ? Encryption.Refuse : null,
                OutgoingEncryption = directionalOverrides ? Encryption.Refuse : null,
                PreferRc4 = preferRc4
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
