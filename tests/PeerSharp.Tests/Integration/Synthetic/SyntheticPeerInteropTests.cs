using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// Interop defects found against libtorrent, pinned here against a peer we control instead.
///
/// <para>
/// Each of these cost a long investigation against a real libtorrent, where the only visible symptom
/// was that a magnet never resolved. They are all cheap and immediate to state against a synthetic
/// peer, because the question in every case is "what bytes did we put on the wire" - and that is a
/// question a conformant client can never be made to ask. libtorrent will not assign ut_metadata an
/// awkward id to see whether we route by it, and will not hang up mid-handshake on demand.
/// </para>
///
/// <para>
/// They share one shape, which is worth naming: PeerSharp agreeing with itself and with nothing else.
/// A self-contained test suite cannot see any of them, because the thing being asserted and the thing
/// doing the asserting were the same code.
/// </para>
/// </summary>
[Collection("Integration")]
public class SyntheticPeerInteropTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public SyntheticPeerInteropTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpSynthetic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// BEP 21: a torrent without metadata must not say it wants nothing.
    ///
    /// <para>
    /// "Is everything I selected already here" answered itself with zero of zero on a magnet, and came
    /// out true the moment the torrent was added - so <c>upload_only</c> went into the extension
    /// handshake before there was a file list. libtorrent reads that as a peer with nothing to gain
    /// from the connection and disconnects it (<c>disconnect_if_redundant</c>), which meant no magnet
    /// could fetch metadata from libtorrent at all. The symptom was a hang; the cause was one key in a
    /// dictionary.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AMagnetDoesNotAdvertiseUploadOnlyBeforeItHasMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = 3, ["ut_pex"] = 4 },
            MetadataSize = 32 * 1024
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        var handshake = await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken);

        long uploadOnly = SyntheticBencode.TryGetInteger(handshake, "upload_only") ?? 0;

        Assert.True(
            uploadOnly == 0,
            "The extension handshake advertised upload_only while the torrent still had no metadata, so a " +
            "peer that honours BEP 21 has been told we want nothing and will drop the connection - taking " +
            "the metadata we were about to ask it for with it.");
    }

    /// <summary>
    /// BEP 10: extension messages are addressed by the receiver's numbering, not the sender's.
    ///
    /// <para>
    /// Extension ids are chosen independently by each side, and a message must carry the id the
    /// <em>recipient</em> published for that extension. PeerSharp sent its ut_metadata request twice -
    /// once correctly addressed, and once under its own local id. Whatever that second id means to the
    /// peer, it is not ut_metadata: at best it is ignored, at worst it is a protocol error and the
    /// connection ends.
    /// </para>
    ///
    /// <para>
    /// The ids here are picked so a confusion cannot pass unnoticed. Nothing in the exchange should
    /// ever be addressed to an id this peer did not publish.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ExtensionMessagesAreAddressedOnlyToIdsThisPeerPublished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Deliberately unusual and deliberately not what PeerSharp assigns locally, so an id taken from
        // the wrong side of the connection cannot coincide with a correct one.
        const byte UtMetadataId = 7;
        const byte UtPexId = 9;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = UtMetadataId, ["ut_pex"] = UtPexId },
            MetadataSize = 32 * 1024
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken);

        // Give the metadata request time to be sent and, if the defect is present, to be sent twice.
        await connection.WaitForFrameAsync(
            static frame => frame.IsExtended && frame.ExtendedId == UtMetadataId,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        byte[] published = [0, UtMetadataId, UtPexId];
        var misaddressed = connection.ExtendedFrames
            .Where(frame => !published.Contains(frame.ExtendedId))
            .ToArray();

        Assert.True(
            misaddressed.Length == 0,
            $"An extension message was addressed to id(s) " +
            $"{string.Join(", ", misaddressed.Select(static frame => frame.ExtendedId).Distinct())}, which this " +
            $"peer never published. BEP 10 ids are chosen per receiver, so that id means something else here - " +
            $"or nothing at all. Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// A peer that hangs up mid-handshake is offered the other encryption choice, and is dialled
    /// again so the offer can actually be made.
    ///
    /// <para>
    /// Encryption support cannot be discovered without trying, so a failed handshake flips what is
    /// offered next time. That only helps if there is a next time, and dialling is driven by peer
    /// supply - a swarm re-announces its peers constantly, but a peer added by hand or found on the
    /// LAN is offered once. The flipped preference was recorded and never used, so a libtorrent built
    /// without encryption was unreachable rather than slow.
    /// </para>
    ///
    /// <para>
    /// The peer is supplied exactly once here. Everything after that has to come from PeerSharp.
    /// </para>
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task APeerThatHangsUpDuringEncryptionIsRedialledInPlaintext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions { HangUpDuringHandshake = true });

        await using var engine = CreateEngine(Encryption.Allow);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        // Supplied once, and never again - which is the condition the defect needed.
        engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);

        var first = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(30), cancellationToken);

        Assert.False(
            first.StartedWithPlaintextHandshake,
            "In Allow mode a peer we know nothing about should be offered encryption first, so this test is " +
            "no longer exercising the fallback it was written for.");

        var second = await peer.WaitForConnectionAsync(1, TimeSpan.FromSeconds(30), cancellationToken);

        Assert.True(
            second.StartedWithPlaintextHandshake,
            "The peer was dialled a second time but was offered encryption again, so a peer that cannot speak " +
            "MSE is never reached at all. The point of alternating is that the second attempt differs from " +
            "the first.");
    }

    private ClientEngine CreateEngine(Encryption encryption)
    {
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
                Encryption = encryption
            },
            Dht = { Enabled = false }
        };

        return ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });
    }

    /// <summary>A magnet with no metadata and no way to get any except the peer the test provides.</summary>
    private async Task<ITorrent> AddMagnetAsync(ClientEngine engine, CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);

        var magnet = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20))}&dn=Synthetic");

        var torrent = await engine.AddMagnetAsync(
            magnet,
            new AddTorrentOptions { StartImmediately = true, DownloadPath = _path });

        return torrent;
    }

    /// <summary>
    /// Points the engine at the synthetic peer and waits for the connection. The address is re-offered
    /// while waiting because these tests are not about how dialling is scheduled - the one test that is
    /// supplies the peer once and does its own waiting.
    /// </summary>
    private static async Task<SyntheticConnection> DialAsync(
        ClientEngine engine, ITorrent torrent, SyntheticPeer peer, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (peer.ConnectionCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);
            await Task.Delay(250, cancellationToken);
        }

        return await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(10), cancellationToken);
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
