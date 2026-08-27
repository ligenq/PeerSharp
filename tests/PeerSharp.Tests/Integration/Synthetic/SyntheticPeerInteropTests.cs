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

        // Asserted by the shared conformance checks, which libtorrent is put through unchanged.
        ExtensionProtocolConformance.AssertNoUploadOnlyBeforeMetadata(handshake, "PeerSharp", isReference: false);
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
        bool metadataRequestArrived = await connection.WaitForFrameAsync(
            static frame => frame.IsExtended && frame.ExtendedId == UtMetadataId,
            TimeSpan.FromSeconds(20),
            cancellationToken);

        Assert.True(
            metadataRequestArrived,
            $"PeerSharp never sent the metadata request whose extension id this test measures. " +
            $"Traffic: {connection.Describe()}");

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connection, [0, UtMetadataId, UtPexId], "PeerSharp", isReference: false);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connection, UtMetadataId, 32 * 1024, "PeerSharp", isReference: false);
    }

    /// <summary>BEP 10: assigning id zero means ut_metadata is disabled, not addressed as id zero.</summary>
    [Fact(Timeout = 60000)]
    public async Task ADisabledMetadataExtensionIsNotAddressedAsTheHandshake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = 0 },
            MetadataSize = 32 * 1024
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        ExtensionProtocolConformance.AssertNoMetadataRequests(connection, "PeerSharp", isReference: false);
    }

    /// <summary>BEP 10: each connection owns its extension numbering independently.</summary>
    [Fact(Timeout = 90000)]
    public async Task MetadataExtensionIdsAreKeptPerConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const byte FirstId = 7;
        const byte SecondId = 11;
        const int MetadataSize = 32 * 1024;

        await using var firstPeer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = FirstId },
            MetadataSize = MetadataSize
        });
        await using var secondPeer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = SecondId },
            MetadataSize = MetadataSize
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        SyntheticConnection[] connections = await Task.WhenAll(
            DialAsync(engine, torrent, firstPeer, cancellationToken),
            DialAsync(engine, torrent, secondPeer, cancellationToken));

        await Task.WhenAll(connections.Select(connection =>
            connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken)));

        bool[] requestsArrived = await Task.WhenAll(
            connections[0].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == FirstId,
                TimeSpan.FromSeconds(20), cancellationToken),
            connections[1].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == SecondId,
                TimeSpan.FromSeconds(20), cancellationToken));

        Assert.All(requestsArrived, Assert.True);

        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connections[0], [0, FirstId], "PeerSharp", isReference: false);
        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connections[1], [0, SecondId], "PeerSharp", isReference: false);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connections[0], FirstId, MetadataSize, "PeerSharp", isReference: false);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connections[1], SecondId, MetadataSize, "PeerSharp", isReference: false);
    }

    /// <summary>The complete BEP 9 path: request, serve, assemble, hash-check and apply metadata.</summary>
    [Fact(Timeout = 90000)]
    public async Task ACompleteInfoDictionaryIsFetchedFromTheSyntheticPeer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        SyntheticMetadataFixture metadata = SyntheticMetadataFixture.Create();

        const byte UtMetadataId = 7;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = UtMetadataId },
            Metadata = metadata.InfoBytes
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken, metadata.InfoHash);
        var connection = await DialAsync(engine, torrent, peer, cancellationToken);

        await torrent.WaitForMetadataAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.True(torrent.HasMetadata);
        Assert.Equal(1, torrent.FileCount);
        Assert.Equal("synthetic-metadata.bin", torrent.GetFileInfo(0).Path);
        Assert.Equal(
            Enumerable.Range(0, metadata.MetadataPieceCount),
            connection.ServedMetadataPieces.Distinct().Order());
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connection, UtMetadataId, metadata.InfoBytes.Length, "PeerSharp", isReference: false);
    }

    /// <summary>The BEP 11 decoder accepts independently encoded PEX and dials the introduced peer.</summary>
    [Fact(Timeout = 90000)]
    public async Task APeerIntroducedOnlyBySyntheticPexIsDialled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var target = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var source = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = 9 },
            PexAdded = { target.EndPoint }
        });

        await using var engine = CreateEngine(Encryption.Refuse);
        var torrent = await AddMagnetAsync(engine, cancellationToken);

        // The target is never supplied to PeerSharp through discovery. Its endpoint exists only in
        // the raw compact PEX message emitted by source.
        await DialAsync(engine, torrent, source, cancellationToken);
        SyntheticConnection introduced = await target.WaitForConnectionAsync(
            0, TimeSpan.FromSeconds(30), cancellationToken);

        Assert.True(introduced.StartedWithPlaintextHandshake);
    }

    /// <summary>The BEP 11 encoder publishes the other connected peer in receiver-owned numbering.</summary>
    [Fact(Timeout = 90000)]
    public async Task PexIntroducesConnectedPeersUsingEachReceiversExtensionId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const byte FirstPexId = 9;
        const byte SecondPexId = 11;

        await using var first = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = FirstPexId }
        });
        await using var second = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = SecondPexId }
        });

        await using var engine = CreateEngine(Encryption.Refuse, TimeSpan.FromSeconds(1));
        var torrent = await AddMagnetAsync(engine, cancellationToken);
        SyntheticConnection[] connections = await Task.WhenAll(
            DialAsync(engine, torrent, first, cancellationToken),
            DialAsync(engine, torrent, second, cancellationToken));

        bool[] pexArrived = await Task.WhenAll(
            connections[0].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == FirstPexId,
                TimeSpan.FromSeconds(20), cancellationToken),
            connections[1].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == SecondPexId,
                TimeSpan.FromSeconds(20), cancellationToken));

        Assert.All(pexArrived, Assert.True);
        ExtensionProtocolConformance.AssertPexIntroduces(
            connections[0], FirstPexId, second.EndPoint, "PeerSharp", isReference: false);
        ExtensionProtocolConformance.AssertPexIntroduces(
            connections[1], SecondPexId, first.EndPoint, "PeerSharp", isReference: false);
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

        // What matters is that plaintext is offered at all without the peer being supplied again, not
        // that it is exactly the second socket. This peer refuses every attempt, and MaxFastReconnects
        // is two, so the engine works through encryption, plaintext and encryption again - and which of
        // those the listener accepts first is not something a loaded machine guarantees. Asserting the
        // index failed once in CI and could not be reproduced in seven runs here, including under full
        // suite load, so the assertion now says what the alternation is for.
        bool plaintextOffered = await SyntheticPeer.WaitForAsync(
            () => peer.Connections.Any(static c => c.StartedWithPlaintextHandshake),
            TimeSpan.FromSeconds(30),
            cancellationToken);

        Assert.True(
            plaintextOffered,
            "The peer was redialled but never offered plaintext, so one that cannot speak MSE is never " +
            "reached at all. The point of alternating is that a later attempt differs from the first. " +
            $"Attempts seen: [{Describe(peer)}]");
    }

    /// <summary>Renders what was offered on each dial, for an assertion message worth reading.</summary>
    private static string Describe(SyntheticPeer peer) => string.Join(
        ", ",
        peer.Connections.Select(static c => c.StartedWithPlaintextHandshake ? "plaintext" : "encrypted"));

    private ClientEngine CreateEngine(Encryption encryption, TimeSpan? pexInterval = null)
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
                Encryption = encryption,
                PexInterval = pexInterval ?? TimeSpan.FromSeconds(60)
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
    private async Task<ITorrent> AddMagnetAsync(
        ClientEngine engine,
        CancellationToken cancellationToken,
        byte[]? infoHash = null)
    {
        await engine.InitializeAsync(cancellationToken);

        var magnet = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{Convert.ToHexString(infoHash ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(20))}&dn=Synthetic");

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
