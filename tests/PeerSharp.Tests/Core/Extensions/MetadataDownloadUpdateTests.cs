using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Reflection;
using PeerSharp.Messages;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// Tests for MetadataDownload.Update() paths not covered by MetadataDownloadTests.cs:
/// timeout detection + retry with alternate peer, and FillMissingRequests on Update.
/// </summary>
public class MetadataDownloadUpdateTests
{
    private class MockUtMetadata : IUtMetadata
    {
        public int? LocalMessageId { get; private set; }
        public int? RemoteMessageId { get; set; }
        public List<int> RequestedPieces { get; } = [];
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public void SendRequest(int piece) => RequestedPieces.Add(piece);
        public List<(int Piece, byte[] Data, int TotalSize)> SentDataPieces { get; } = [];
        public List<int> RejectedPieces { get; } = [];
        public void SendData(int piece, byte[] data, int totalSize) => SentDataPieces.Add((piece, data, totalSize));
        public void SendReject(int piece) => RejectedPieces.Add(piece);
    }

    private class MockPeerCommunication : IPeerCommunication
    {
        public byte[] PeerId { get; set; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
        public bool RemoteSupportsExtensions { get; set; }
        public bool RemoteIsUploadOnly { get; set; }
        public PiecesProgress PeerPieces { get; set; } = new(16);
        public ExtensionHandshake? RemoteExtensions { get; set; }
        public IUtMetadata UtMetadata { get; set; } = null!;
        public IUtPex UtPex => throw new NotImplementedException();
        public IPeerListener Listener => throw new NotImplementedException();
        public IUtHashPiece? UtHashPiece => throw new NotImplementedException();
        public IUtHolepunch UtHolepunch => throw new NotImplementedException();
        public List<PeerMessage> SentMessages { get; } = [];
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg) { SentMessages.Add(msg); return Task.CompletedTask; }
    }

    private static MockPeerCommunication MakePeer(int? metadataSize = null)
    {
        var utMeta = new MockUtMetadata { RemoteMessageId = 1 };
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = metadataSize },
            UtMetadata = utMeta,
        };
        peer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;
        return peer;
    }

    private static void InjectActivePeer(MetadataDownload download, IPeerCommunication peer)
    {
        var field = typeof(MetadataDownload)
            .GetField("_activePeers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<IPeerCommunication>)field.GetValue(download)!).Add(peer);
    }

    private static void InjectPendingRequest(MetadataDownload download, int pieceIndex, IPeerCommunication peer, DateTimeOffset timestamp, int attempts = 1)
    {
        download.SetPendingRequestForTesting(peer, pieceIndex, timestamp, attempts);
    }

    [Fact]
    public void Update_TimedOutRequest_ReRequestsFromAlternatePeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize); // 1 piece

        var peer1 = MakePeer(UtMetadata.PieceSize);
        var peer2 = MakePeer(UtMetadata.PieceSize);
        InjectActivePeer(download, peer1);
        InjectActivePeer(download, peer2);

        // Inject a stale pending request for peer1 (far in the past)
        InjectPendingRequest(download, 0, peer1, DateTimeOffset.UtcNow.AddSeconds(-60));

        download.Update();

        // peer2 should now have piece 0 requested (since peer1's request timed out)
        Assert.Contains(0, ((MockUtMetadata)peer2.UtMetadata).RequestedPieces);
    }

    [Fact]
    public void Update_TimedOutMaxAttempts_DoesNotReRequest()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        // Pipeline=1 keeps piece 1 as the only active distinct piece after piece 0 times out.
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        torrent.Settings.Transfer.MetadataMaxRequestAttempts = 1; // max 1 attempt
        var download = new MetadataDownload(torrent);
        download.Start();
        // 2 pieces so piece 1 is a valid index
        download.InitializeMetadataBuffer(UtMetadata.PieceSize * 2);

        var peer1 = MakePeer(UtMetadata.PieceSize * 2);
        var peer2 = MakePeer(UtMetadata.PieceSize * 2);
        InjectActivePeer(download, peer1);
        InjectActivePeer(download, peer2);

        // Inject stale request for piece 0 that has used all attempts
        InjectPendingRequest(download, 0, peer1, DateTimeOffset.UtcNow.AddSeconds(-60), attempts: 1 /* >= MaxRequestAttempts=1 */);
        // Inject a fresh (non-timed-out) request for piece 1 — this fills the pipeline
        InjectPendingRequest(download, 1, peer2, DateTimeOffset.UtcNow, attempts: 1);

        download.Update();

        // Timeout handler drops piece 0 (max attempts exceeded) without re-requesting.
        // The peer-driven scheduler sees one active distinct piece at the pipeline limit.
        // So peer2 should receive no new SendRequest calls.
        Assert.Empty(((MockUtMetadata)peer2.UtMetadata).RequestedPieces);
    }

    [Fact]
    public void Update_FillsMissingPieces()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 10;
        var download = new MetadataDownload(torrent);
        download.Start();
        // 2-piece metadata
        download.InitializeMetadataBuffer(UtMetadata.PieceSize * 2);

        var peer = MakePeer(UtMetadata.PieceSize * 2);
        InjectActivePeer(download, peer);

        download.Update();

        var utMeta = (MockUtMetadata)peer.UtMetadata;
        Assert.Contains(0, utMeta.RequestedPieces);
        Assert.Contains(1, utMeta.RequestedPieces);
    }

    [Fact]
    public void Update_AfterRepeatedSilence_PostsOneDiagnosticAlert()
    {
        var time = new FakeTimeProvider();
        var alerts = new AlertsManager(time);
        alerts.RegisterAlerts((uint)AlertId.MetadataDownloadStalled);
        var torrent = TorrentTestUtility.CreateMinimal(timeProvider: time, alerts: alerts);
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(UtMetadata.PieceSize);

        InjectActivePeer(download, MakePeer(UtMetadata.PieceSize));
        InjectActivePeer(download, MakePeer(UtMetadata.PieceSize));
        InjectActivePeer(download, MakePeer(UtMetadata.PieceSize));

        download.Update();
        time.Advance(TimeSpan.FromSeconds(11));
        download.Update();
        time.Advance(TimeSpan.FromSeconds(20));
        download.Update();
        download.Update();

        var alert = Assert.IsType<MetadataDownloadStalledAlert>(Assert.Single(alerts.PopAlerts()));
        Assert.Equal(3, alert.CapablePeers);
        Assert.True(alert.RequestsSent >= 6);
        Assert.True(alert.Elapsed >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Dispose_StopsDownload()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        download.Dispose();

        Assert.False(download.Active);
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        download.Dispose();
        download.Dispose(); // must not throw
    }
}
