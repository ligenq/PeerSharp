using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// A peer that goes away is forgotten by the metadata fetch.
///
/// <para>
/// <c>MetadataDownload.PeerDisconnected</c> existed, was unit tested, and was called from nowhere in
/// the engine. So a peer that dropped stayed on the active list for good: the recovery that only runs
/// when no peers remain was permanently suppressed, requests kept being addressed to a closed
/// connection, and the CLI could report no peers while the metadata download still counted one.
/// </para>
///
/// <para>
/// The unit tests could not see it, because they called the method themselves. This one closes a real
/// socket and asks the engine what it believes afterwards, which is the only version of the question
/// that was ever in doubt.
/// </para>
/// </summary>
[Collection("Integration")]
public class SyntheticPeerMetadataDisconnectTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public SyntheticPeerMetadataDisconnectTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpMetaDisc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    [Fact(Timeout = 120000)]
    public async Task AMetadataPeerThatDisconnectsIsRemovedFromTheActiveList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var settings = new Settings
        {
            Files = { DefaultDownloadPath = _path },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                EnableUtpIn = false,
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = false }
        };

        await using var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });
        await engine.InitializeAsync(cancellationToken);

        var magnet = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20))}&dn=Disconnect");
        var torrent = (Torrent)await engine.AddMagnetAsync(
            magnet, new AddTorrentOptions { StartImmediately = true, DownloadPath = _path });

        var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = 3 },
            MetadataSize = 32 * 1024
        });

        try
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (peer.ConnectionCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);
                await Task.Delay(250, cancellationToken);
            }

            var connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(10), cancellationToken);
            await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken);

            var download = torrent.MetadataDownloadInternal;
            Assert.NotNull(download);

            bool registered = await SyntheticPeer.WaitForAsync(
                () => download.ActivePeerCountForTesting > 0, TimeSpan.FromSeconds(30), cancellationToken);

            Assert.True(registered, "The peer advertised ut_metadata but was never taken up as a metadata source.");

            // Take the socket away, the way a peer leaving a swarm does.
            await peer.DisposeAsync();

            bool forgotten = await SyntheticPeer.WaitForAsync(
                () => download.ActivePeerCountForTesting == 0, TimeSpan.FromSeconds(30), cancellationToken);

            Assert.True(
                forgotten,
                $"The peer is gone but the metadata download still counts " +
                $"{download.ActivePeerCountForTesting} of them. Requests go to a closed connection, and the " +
                "recovery that restarts exploration only runs when the active list is empty - so this " +
                "magnet can no longer resolve from any peer that arrives later.");
        }
        finally
        {
            await peer.DisposeAsync();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
