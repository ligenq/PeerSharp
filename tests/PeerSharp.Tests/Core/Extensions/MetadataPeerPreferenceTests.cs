using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// Choosing which peer to ask for metadata, once there is evidence about who answers.
///
/// <para>
/// Advertising ut_metadata with a size says a peer holds the metadata, not that it will part with it,
/// and in a real swarm most will not. Traced against Ubuntu's magnet: of 75 peers asked, 8 ever
/// answered, and no peer ever sent a reject - the rest simply stayed silent. The willing group barely
/// grows with the swarm, so drawing uniformly at random gave a hit rate that fell as the swarm grew.
/// Across eight runs of the same magnet the effect was monotonic: 28 connected peers collected sixteen
/// metadata pieces in 6.5 seconds, 148 peers took 55. Having more peers made it slower.
/// </para>
///
/// <para>
/// So a peer that has answered is preferred, a peer that has been asked repeatedly and stayed silent is
/// a last resort, and a peer nobody has tried yet sits between them - which is also what keeps the
/// proven set growing rather than freezing around whoever answered first.
/// </para>
/// </summary>
public class MetadataPeerPreferenceTests
{
    private const int PieceSize = UtMetadata.PieceSize;

    [Fact]
    public async Task APeerThatHasAnsweredIsPreferredOverOnesThatNeverDid()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize * 4);

        var willing = MakePeer();
        var silent = Enumerable.Range(0, 8).Select(_ => MakePeer()).ToList();
        InjectActivePeer(download, willing);
        foreach (var peer in silent)
        {
            InjectActivePeer(download, peer);
        }

        // The willing peer answers one piece; everyone else has been asked and said nothing. Enough
        // silence to pass the demotion threshold, so the tiers are unambiguous.
        await download.MetadataPieceReceivedAsync(willing, 0, new byte[PieceSize]);
        foreach (var peer in silent)
        {
            RecordTimedOut(download, peer, count: 5);
        }

        // Receiving a piece fills the pipeline again, so clear it or there is nothing left to assign.
        ClearPending(download);

        download.Update();

        // Ownership rather than "was sent anything": the redundant asks in AskAdditionalPeers
        // deliberately spill into the lower tiers once the better ones are exhausted, so a silent peer
        // seeing some traffic is correct. What tiering decides is who gets asked first, and that is the
        // peer recorded against each pending request.
        var owners = PendingOwners(download);
        Assert.NotEmpty(owners);
        Assert.All(owners, owner => Assert.Same(willing, owner));
    }

    [Fact]
    public void AnUntriedPeerIsPreferredOverOneKnownToBeSilent()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize * 4);

        var untried = MakePeer();
        var silent = MakePeer();
        InjectActivePeer(download, untried);
        InjectActivePeer(download, silent);

        RecordTimedOut(download, silent, count: 5);

        download.Update();

        // Nothing here depends on a peer having answered: the middle tier is what lets the proven set
        // grow past whoever happened to answer first, so an untried peer must outrank a known-silent
        // one on no evidence at all.
        var owners = PendingOwners(download);
        Assert.NotEmpty(owners);
        Assert.All(owners, owner => Assert.Same(untried, owner));
    }

    [Fact]
    public void ASilentPeerIsStillAskedWhenThereIsNobodyBetter()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize);

        var silent = MakePeer();
        InjectActivePeer(download, silent);
        RecordTimedOut(download, silent, count: 20);
        ClearRequests(silent);

        download.Update();

        // Demotion orders the candidates; it must never empty them. A swarm where nothing has answered
        // yet is the normal state for the first second of every magnet.
        Assert.NotEmpty(Requests(silent));
    }

    private static MockPeerCommunication MakePeer(int? metadataSize = PieceSize)
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

    private static void ClearRequests(IPeerCommunication peer) => Requests(peer).Clear();

    private static System.Collections.IDictionary Pending(MetadataDownload download)
    {
        var field = typeof(MetadataDownload)
            .GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (System.Collections.IDictionary)field.GetValue(download)!;
    }

    private static void ClearPending(MetadataDownload download) => Pending(download).Clear();

    /// <summary>The peer recorded against each outstanding request - the one the tiers chose.</summary>
    private static List<IPeerCommunication> PendingOwners(MetadataDownload download)
    {
        var pendingType = typeof(MetadataDownload)
            .GetNestedType("PendingMetadataRequest", BindingFlags.NonPublic)!;
        var peerProperty = pendingType.GetProperty("Peer")!;

        var owners = new List<IPeerCommunication>();
        foreach (System.Collections.DictionaryEntry entry in Pending(download))
        {
            owners.Add((IPeerCommunication)peerProperty.GetValue(entry.Value)!);
        }

        return owners;
    }

    private static void InjectActivePeer(MetadataDownload download, IPeerCommunication peer)
    {
        var field = typeof(MetadataDownload)
            .GetField("_activePeers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<IPeerCommunication>)field.GetValue(download)!).Add(peer);
    }

    /// <summary>
    /// Marks a peer as having timed out this many times, which is the state the selection is meant to
    /// react to and takes a live swarm several seconds to produce.
    /// </summary>
    private static void RecordTimedOut(MetadataDownload download, IPeerCommunication peer, int count)
    {
        var field = typeof(MetadataDownload)
            .GetField("_peerRecords", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var records = (System.Collections.IDictionary)field.GetValue(download)!;

        var recordType = typeof(MetadataDownload)
            .GetNestedType("MetadataPeerRecord", BindingFlags.NonPublic)!;
        var record = Activator.CreateInstance(recordType)!;
        recordType.GetProperty("TimedOut")!.SetValue(record, count);

        records[peer] = record;
    }

    private class MockUtMetadata : IUtMetadata
    {
        public int? LocalMessageId { get; private set; }
        public int? RemoteMessageId { get; set; }
        public List<int> RequestedPieces { get; } = [];
        public List<(int Piece, byte[] Data, int TotalSize)> SentDataPieces { get; } = [];
        public List<int> RejectedPieces { get; } = [];
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public void SendRequest(int piece) => RequestedPieces.Add(piece);
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
}
