using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// The order of the first messages we send after a handshake.
///
/// <para>
/// BEP 3 is unambiguous: "'bitfield' is only ever sent as the first message." Strict clients discard a
/// bitfield that arrives after anything else, and a peer that believes we hold nothing will never ask
/// us for a byte. We used to send the BEP 5 Port message ahead of it whenever DHT was enabled, which
/// is the default in production and disabled in every local test.
/// </para>
///
/// <para>
/// This has to be asserted on what we <em>send</em>, because our own parser tolerates the wrong order -
/// it exempts Port and Extended from its own first-message check. Two PeerSharp instances therefore
/// interoperate happily while real clients ignore us, which is exactly how the bug survived a full
/// local suite and only showed up when seeding to a live swarm.
/// </para>
/// </summary>
public class HandshakeMessageOrderTests : IDisposable
{
    private static readonly byte[] TestInfoHash =
    [
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA,
        0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01, 0x02, 0x03, 0x04, 0x05
    ];

    private readonly string _path;

    public HandshakeMessageOrderTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpMsgOrder_" + Guid.NewGuid().ToString("N"));
    }

    [Fact(Timeout = 30000)]
    public async Task PieceAdvertisementPrecedesThePortMessage()
    {
        var (torrent, manager) = CreateContext(dhtEnabled: true);
        try
        {
            // A complete torrent, so there is definitely something to advertise.
            torrent.Pieces.SetHaveAll();

            var peer = new RecordingPeer(torrent, manager, TimeProvider.System);
            await manager.HandshakeFinishedAsync(peer);

            int advertisement = peer.Sent.FindIndex(static id =>
                id is MessageId.Bitfield or MessageId.HaveAll or MessageId.HaveNone);
            int port = peer.Sent.IndexOf(MessageId.Port);

            Assert.True(advertisement >= 0, $"No piece advertisement was sent at all. Sent: {peer.Describe()}");
            Assert.True(port >= 0, $"No Port message was sent, so this test is not exercising the ordering. Sent: {peer.Describe()}");
            Assert.True(
                advertisement < port,
                $"The Port message was sent before our piece advertisement, so a strict peer will discard the " +
                $"bitfield and never request anything from us. Sent: {peer.Describe()}");
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task PieceAdvertisementIsTheVeryFirstMessage()
    {
        // Stronger than the ordering above and what BEP 3 actually says. Anything that creeps in ahead
        // of the bitfield later - a new extension, a keepalive - reintroduces the same bug.
        var (torrent, manager) = CreateContext(dhtEnabled: true);
        try
        {
            torrent.Pieces.SetHaveAll();

            var peer = new RecordingPeer(torrent, manager, TimeProvider.System);
            await manager.HandshakeFinishedAsync(peer);

            Assert.NotEmpty(peer.Sent);
            Assert.True(
                peer.Sent[0] is MessageId.Bitfield or MessageId.HaveAll or MessageId.HaveNone,
                $"The first message after the handshake was {peer.Sent[0]}, not a piece advertisement. " +
                $"BEP 3: \"'bitfield' is only ever sent as the first message.\" Sent: {peer.Describe()}");
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task SuperSeedingStillAdvertisesBeforeThePortMessage()
    {
        // Superseeding advertises "nothing" instead of everything, but the ordering rule is the same.
        var (torrent, manager) = CreateContext(dhtEnabled: true);
        try
        {
            torrent.Pieces.SetHaveAll();
            torrent.SuperSeedManager.Enabled = true;

            var peer = new RecordingPeer(torrent, manager, TimeProvider.System);
            await manager.HandshakeFinishedAsync(peer);

            int advertisement = peer.Sent.FindIndex(static id =>
                id is MessageId.Bitfield or MessageId.HaveAll or MessageId.HaveNone);
            int port = peer.Sent.IndexOf(MessageId.Port);

            Assert.True(advertisement >= 0, $"Superseeding sent no piece advertisement. Sent: {peer.Describe()}");
            Assert.True(
                port < 0 || advertisement < port,
                $"Superseeding sent the Port message before its advertisement. Sent: {peer.Describe()}");
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    private (Torrent Torrent, PeerManager Manager) CreateContext(bool dhtEnabled)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(TestInfoHash);
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Pieces = [new byte[20]];
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata, _path);
        torrent.Settings.Dht.Enabled = dhtEnabled;

        if (dhtEnabled)
        {
            // The Port message is only sent when a DHT manager is actually present.
            torrent.DhtManager = new StubDhtManager();
        }

        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new PeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor());

        return (torrent, manager);
    }

    private static void Cleanup(Torrent torrent, PeerManager manager)
    {
        try { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
        try { torrent.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

    /// <summary>
    /// Records the order of outgoing messages. Every send funnels through the virtual
    /// <see cref="PeerCommunication.SendMessageAsync"/>, so overriding it captures everything.
    /// </summary>
    private sealed class RecordingPeer : PeerCommunication
    {
        public RecordingPeer(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6881);
            Connected = 1;

            // A distinct peer id, so identity resolution does not mistake this for a self-connection.
            for (int i = 0; i < PeerId.Length; i++)
            {
                PeerId[i] = (byte)(0xA0 + i);
            }
        }

        public List<MessageId> Sent { get; } = [];

        public override Task SendMessageAsync(PeerMessage msg)
        {
            Sent.Add(msg.Id);
            return Task.CompletedTask;
        }

        public string Describe() => Sent.Count == 0 ? "(nothing)" : string.Join(" -> ", Sent);
    }

    private sealed class StubDhtManager : Internals.Dht.IDhtManager
    {
        public InfoHash NodeId { get; } = new(new byte[20]);

        public void Announce(InfoHash infoHash, int port) { }
        public void FindPeers(InfoHash infoHash) { }
        public void Ping(IPEndPoint ep) { }
        public void ReportExternalIp(IPAddress address) { }
        public void ScrapeInfoHash(InfoHash infoHash) { }
        public void SetCallback(Internals.Dht.IDhtCallback callback) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public DhtState? ConsumeStateSnapshot() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
