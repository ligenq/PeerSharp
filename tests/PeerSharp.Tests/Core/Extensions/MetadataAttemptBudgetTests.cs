using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// What happens when every peer runs out of attempts.
///
/// <para>
/// Attempts are counted per peer and per piece, which is what stops one silent peer from being asked
/// the same thing forever. The counter has no other half: nothing decrements it and nothing expires it,
/// so a swarm that times out enough rounds can spend every (peer, piece) budget it has. The download is
/// then indistinguishable from one with no peers at all - except that the peers are still connected,
/// still advertising the metadata, and will never be asked again.
/// </para>
///
/// <para>
/// The default timeout is one second, which a peer on a slow link misses routinely, so this is reached
/// by ordinary latency rather than by malice.
/// </para>
/// </summary>
public class MetadataAttemptBudgetTests
{
    private const int PieceSize = UtMetadata.PieceSize;

    [Fact]
    public void ExhaustingEveryPeerBudget_StillLeavesTheDownloadSchedulable()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var torrent = TorrentTestUtility.CreateMinimal(timeProvider: time);
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        torrent.Settings.Transfer.MetadataRequestRedundancy = 1;
        torrent.Settings.Transfer.MetadataMaxRequestAttempts = 2;
        torrent.Settings.Transfer.MetadataRequestTimeoutSeconds = 1;

        var download = new MetadataDownload(torrent);
        download.Start();
        var peers = Enumerable.Range(0, 2).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        // One piece, one owner at a time, two attempts each: four timeout rounds spends the lot.
        download.InitializeMetadataBuffer(PieceSize);
        Assert.NotEmpty(download.GetPendingRequestsForTesting());

        for (int round = 0; round < 8; round++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            download.Update();
        }

        // Nobody has answered and nobody has left. The piece is still missing, so there is still work,
        // and there are two peers advertising that they hold it - so something must be outstanding.
        Assert.NotEmpty(download.GetPendingRequestsForTesting());
        Assert.All(peers, peer => Assert.NotEmpty(Requests(peer)));
    }

    [Fact]
    public void RestartingExploration_DoesNotAbandonPiecesAlreadyReceived()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var torrent = TorrentTestUtility.CreateMinimal(timeProvider: time);
        torrent.Settings.Transfer.MetadataRequestPipeline = 2;
        torrent.Settings.Transfer.MetadataRequestRedundancy = 1;
        torrent.Settings.Transfer.MetadataMaxRequestAttempts = 1;
        torrent.Settings.Transfer.MetadataRequestTimeoutSeconds = 1;

        var download = new MetadataDownload(torrent);
        download.Start();
        var peer = MakePeer(PieceSize * 2);
        InjectActivePeer(download, peer);
        download.InitializeMetadataBuffer(PieceSize * 2);

        MarkPieceReceived(download, 0);

        for (int round = 0; round < 6; round++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            download.Update();
        }

        // Whatever the budget does, a piece already in hand must never be asked for again.
        Assert.All(
            download.GetPendingRequestsForTesting(),
            request => Assert.Equal(1, request.Piece));
    }

    /// <summary>
    /// Requests are deliberately redundant, so several peers answer for the same piece and every one
    /// of them is credited with an answer - duplicates included. Only one peer's bytes end up in the
    /// buffer, so only that peer is implicated when the completed set fails its info hash. Refusing on
    /// "answered something" instead convicts the honest majority, and refusal is permanent.
    /// </summary>
    [Fact]
    public async Task CorruptMetadata_OnlyRefusesThePeerWhoseBytesWereUsed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, timeProvider: time);
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;
        torrent.Settings.Transfer.MetadataRequestRedundancy = 3;

        var download = new MetadataDownload(torrent);
        download.Start();
        var supplier = MakePeer(PieceSize * 2);
        var bystanders = Enumerable.Range(0, 3).Select(_ => MakePeer(PieceSize * 2)).ToList();
        InjectActivePeer(download, supplier);
        foreach (var peer in bystanders)
        {
            InjectActivePeer(download, peer);
        }
        download.InitializeMetadataBuffer(PieceSize * 2);

        // The supplier's copy of piece 0 lands first and is the one stored. The bystanders answer the
        // same piece afterwards - redundancy working as intended - and are credited for it without
        // contributing a byte. The supplier then completes the set, which is not a valid info
        // dictionary for this hash.
        await download.MetadataPieceReceivedAsync(supplier, 0, new byte[PieceSize]);
        foreach (var peer in bystanders)
        {
            await download.MetadataPieceReceivedAsync(peer, 0, new byte[PieceSize]);
        }
        await download.MetadataPieceReceivedAsync(supplier, 1, new byte[PieceSize]);

        var refused = RefusedPeers(download);
        Assert.Contains(supplier, refused);
        Assert.All(bystanders, peer => Assert.DoesNotContain(peer, refused));

        // And with peers left to ask, the retry has somewhere to go.
        Assert.NotEmpty(download.GetPendingRequestsForTesting());
    }

    [Fact]
    public void WithNoPeersAtAll_TheStalledDownloadDoesNotSpin()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var torrent = TorrentTestUtility.CreateMinimal(timeProvider: time);
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize);

        for (int round = 0; round < 4; round++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            download.Update();
        }

        Assert.Empty(download.GetPendingRequestsForTesting());
    }

    private static MockPeerCommunication MakePeer(int metadataSize = PieceSize)
    {
        var peer = new MockPeerCommunication
        {
            RemoteSupportsExtensions = true,
            RemoteExtensions = new ExtensionHandshake { MetadataSize = metadataSize },
            UtMetadata = new MockUtMetadata { RemoteMessageId = 1 },
            PeerId = [.. Guid.NewGuid().ToByteArray(), .. new byte[4]],
        };
        peer.RemoteExtensions.MessageIds[UtMetadata.Name] = 1;
        return peer;
    }

    private static List<int> Requests(IPeerCommunication peer)
        => ((MockUtMetadata)peer.UtMetadata).RequestedPieces;

    private static void InjectActivePeer(MetadataDownload download, IPeerCommunication peer)
    {
        var field = typeof(MetadataDownload)
            .GetField("_activePeers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<IPeerCommunication>)field.GetValue(download)!).Add(peer);
    }

    private static List<IPeerCommunication> RefusedPeers(MetadataDownload download)
    {
        var field = typeof(MetadataDownload)
            .GetField("_refusedRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var refused = (IEnumerable<(IPeerCommunication Peer, int Piece)>)field.GetValue(download)!;
        return refused.Select(entry => entry.Peer).Distinct().ToList();
    }

    private static void MarkPieceReceived(MetadataDownload download, int piece)
    {
        var field = typeof(MetadataDownload)
            .GetField("_receivedPieces", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((System.Collections.BitArray)field.GetValue(download)!)[piece] = true;
    }

    private class MockUtMetadata : IUtMetadata
    {
        public int? LocalMessageId { get; private set; }
        public int? RemoteMessageId { get; set; }
        public List<int> RequestedPieces { get; } = [];
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public void SendRequest(int piece) => RequestedPieces.Add(piece);
        public void SendData(int piece, byte[] data, int totalSize) { }
        public void SendReject(int piece) { }
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
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg) => Task.CompletedTask;
    }
}
