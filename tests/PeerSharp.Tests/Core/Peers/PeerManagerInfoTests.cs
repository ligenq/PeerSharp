using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utp;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerInfoTests
{
    [Fact]
    public void GetPieceAvailability_CombinesPeerPieces()
    {
        var ctx = CreateContext(pieceCount: 3);
        var manager = ctx.Manager;

        var peerA = CreatePeer(ctx.Torrent, new IPEndPoint(IPAddress.Loopback, 1));
        peerA.PeerPieces.AddPiece(0);
        peerA.PeerPieces.AddPiece(2);

        var peerB = CreatePeer(ctx.Torrent, new IPEndPoint(IPAddress.Loopback, 2));
        peerB.PeerPieces.AddPiece(1);
        peerB.PeerPieces.AddPiece(2);

        AddConnectedPeer(manager, peerA);
        AddConnectedPeer(manager, peerB);

        int[] availability = manager.GetPieceAvailability();

        Assert.Equal(new[] { 1, 1, 2 }, availability);

        Cleanup(ctx);
    }

    [Fact]
    public void GetConnectedPeers_ReportsEncryptionAndUtpFlags()
    {
        var ctx = CreateContext(pieceCount: 1);
        var manager = ctx.Manager;

        var peer = CreatePeer(ctx.Torrent, new IPEndPoint(IPAddress.Loopback, 1));
        peer.Stream = CreateEncryptedStream();
        peer.UtpStream = new UtpStream(new FakeUtpManager(), new IPEndPoint(IPAddress.Loopback, 1), 1, 2, TimeProvider.System);

        AddConnectedPeer(manager, peer);

        var peers = manager.GetConnectedPeers();

        Assert.Single(peers);
        Assert.True(peers[0].IsEncrypted);
        Assert.True(peers[0].IsUtp);

        Cleanup(ctx);
    }

    private static PeerManagerContext CreateContext(int pieceCount)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * pieceCount;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);

        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new FakePeerFactory(), TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
        return new PeerManagerContext(torrent, manager, path);
    }

    private static PeerCommunication CreatePeer(Torrent torrent, IPEndPoint endpoint)
    {
        var peer = new PeerCommunication(torrent, new TestPeerListener(), TimeProvider.System)
        {
            RemoteEndPoint = endpoint,
            Country = "US"
        };
        return peer;
    }

    private static void AddConnectedPeer(PeerManager manager, PeerCommunication peer)
    {
        manager.AddConnectedPeerForTesting(peer);
    }

    private static Stream CreateEncryptedStream()
    {
        var pe = new ProtocolEncryption();
        var manager = new TestBandwidthManager();
        return new EncryptedStream(new MemoryStream(), pe, new TestBandwidthUser(), manager,
            [BandwidthManager.GlobalDownload], [BandwidthManager.GlobalUpload], leaveInnerOpen: true);
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerManagerInfo", Guid.NewGuid().ToString("N"));
    }

    private static void Cleanup(PeerManagerContext ctx)
    {
        ctx.Manager.StopAsync().GetAwaiter().GetResult();
        ctx.Torrent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(ctx.Path))
            {
                Directory.Delete(ctx.Path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private sealed record PeerManagerContext(Torrent Torrent, PeerManager Manager, string Path);

    private sealed class FakePeerFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }
    }

    private sealed class FakeUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void Start(IUdpListener listener) { }
        public void Stop() { }
        public UtpStream CreateStream(IPEndPoint remote) => null!;
        public void CloseStream(UtpStream stream) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestBandwidthManager : IBandwidthManager
    {
        public void Configure(int updateIntervalMs) { }
        public BandwidthChannel GetChannel(string name) => new BandwidthChannel(TimeProvider.System);
        public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent) => (0, 0);
        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent) => (0, 0);
        public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default) => Task.FromResult(amount);
        public void ReturnBandwidth(int amount, string[] channelNames) { }
        public void RemoveTorrentChannels(ITorrent torrent) { }
        public void SetGlobalLimits(int downloadLimit, int uploadLimit) { }
        public void SetGlobalDiskLimits(int readLimit, int writeLimit) { }
        public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit) { }
        public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit) { }
        public void Start() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestBandwidthUser : IBandwidthUser
    {
        public string Name => "peer";
        public void AssignBandwidth(int amount) { }
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}




