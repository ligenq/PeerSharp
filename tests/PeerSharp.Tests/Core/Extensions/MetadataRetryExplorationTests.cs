using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// Retrying a metadata request that nobody answered.
///
/// <para>
/// A magnet has nothing until metadata arrives, so the retry path is the whole download. Two faults in
/// it left three of four torrents in a live session sitting without metadata for minutes while the
/// fourth succeeded by luck: 302 peers advertised ut_metadata and only 9 were ever asked, the same two
/// alternately, every ten seconds.
/// </para>
///
/// <para>
/// The retry removed the pending request before calling back in to re-request, so the attempt count
/// could not be recovered and reset to 1 every time. That pinned <c>Attempts</c> at 1, which made the
/// give-up check permanently true, so a piece was never released back to the path that picks a fresh
/// peer at random. And the alternate-peer choice returned the first entry that was not the current one,
/// which alternates between the first two peers and reaches nobody else however large the pool.
/// </para>
///
/// <para>
/// The existing coverage did not catch either, because it injects a pending request with the attempt
/// count already set - which is exactly the step that was broken.
/// </para>
/// </summary>
public class MetadataRetryExplorationTests
{
    private const int PieceSize = UtMetadata.PieceSize;

    [Fact]
    public void RepeatedTimeoutsCountUpRatherThanResettingToOne()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize); // One piece.

        var peers = Enumerable.Range(0, 4).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        // Nobody ever answers, so every round times out.
        for (int round = 0; round < 3; round++)
        {
            StalePendingRequest(download, piece: 0, peer: peers[round % peers.Count]);
            download.Update();
        }

        int attempts = CurrentAttempts(download, piece: 0);
        Assert.True(
            attempts > 1,
            $"After three timed-out rounds the request was still on attempt {attempts}. The count resets " +
            "every retry, so the give-up check never fires and the piece is never handed back to the " +
            "random-peer path that would find a different peer.");
    }

    [Fact]
    public void RetriesReachMoreThanTwoPeersWhenManyAreAvailable()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize);

        var peers = Enumerable.Range(0, 12).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        // Time out repeatedly against whoever currently holds the request.
        for (int round = 0; round < 20; round++)
        {
            var holder = PendingPeer(download, piece: 0) ?? peers[0];
            StalePendingRequest(download, piece: 0, peer: holder);
            download.Update();
        }

        int asked = peers.Count(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Count > 0);

        Assert.True(
            asked > 2,
            $"Twenty retries reached only {asked} of {peers.Count} available peers. Choosing the first " +
            "peer that is not the current one makes consecutive retries alternate between the first two " +
            "entries and never reach the rest of the pool.");
    }

    /// <summary>
    /// A piece is asked of several peers at once, so one slow peer cannot hold up the whole magnet.
    ///
    /// <para>
    /// Metadata is at most 16 KiB a piece and nothing can start without it, so the duplicate traffic is
    /// irrelevant next to the latency. Asking one peer and waiting made a magnet only as fast as
    /// whichever peer was picked first.
    /// </para>
    /// </summary>
    [Fact]
    public void APieceIsAskedOfSeveralPeersAtOnce()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();

        var peers = Enumerable.Range(0, 6).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        download.InitializeMetadataBuffer(PieceSize); // Triggers the first request.

        int asked = peers.Count(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Contains(0));

        Assert.True(
            asked > 1,
            $"Piece 0 was asked of {asked} peer(s). A single outstanding request means the magnet waits " +
            "on whichever peer happened to be chosen, however many others are connected.");
    }

    /// <summary>
    /// Redundancy is bounded. Asking every connected peer would turn a large swarm into a broadcast.
    /// </summary>
    [Fact]
    public void TheNumberOfPeersAskedIsBounded()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();

        var peers = Enumerable.Range(0, 40).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        download.InitializeMetadataBuffer(PieceSize);

        int asked = peers.Count(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Contains(0));

        Assert.InRange(asked, 2, 8);
    }

    /// <summary>
    /// The redundant requests must spread out too, not always land on the same neighbours.
    ///
    /// <para>
    /// Taking the first entries that are not the peer just asked sends every round to the same two, so
    /// if those are silent the redundancy buys nothing - the same mistake that made the alternate-peer
    /// choice ping-pong, reintroduced in the code that was meant to fix it.
    /// </para>
    /// </summary>
    [Fact]
    public void RedundantRequestsDoNotAlwaysGoToTheSamePeers()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();

        var peers = Enumerable.Range(0, 12).Select(_ => MakePeer()).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        download.InitializeMetadataBuffer(PieceSize);

        for (int round = 0; round < 20; round++)
        {
            var holder = PendingPeer(download, piece: 0) ?? peers[0];
            StalePendingRequest(download, piece: 0, peer: holder);
            download.Update();
        }

        // Coverage alone does not show this, because the tracked peer is already chosen at random and
        // drags a few others in with it. The skew does: walking the list in order sends nearly every
        // redundant request to whichever peers sit at the front of it.
        var counts = peers
            .Select(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Count)
            .OrderByDescending(n => n)
            .ToList();

        int total = counts.Sum();
        int topTwo = counts[0] + counts[1];

        Assert.True(
            total > 0 && topTwo < total / 2,
            $"The two most-asked peers took {topTwo} of {total} requests. Walking the list in order sends " +
            "every redundant request to the same neighbours, so silent peers at the front are asked over " +
            $"and over while the rest of the pool is barely tried. Distribution: {string.Join(",", counts)}");
    }

    /// <summary>
    /// A peer that speaks ut_metadata but has not said how large the metadata is cannot answer, so it
    /// should not be chosen while peers that declared a size are available.
    /// </summary>
    [Fact]
    public void PeersThatActuallyHoldTheMetadataArePreferred()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();

        // Ten that only speak the extension, two that hold the data.
        var empty = Enumerable.Range(0, 10).Select(_ => MakePeer(metadataSize: null)).ToList();
        var holders = Enumerable.Range(0, 2).Select(_ => MakePeer()).ToList();
        foreach (var peer in empty.Concat(holders))
        {
            InjectActivePeer(download, peer);
        }

        download.InitializeMetadataBuffer(PieceSize);

        int askedHolders = holders.Count(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Count > 0);
        int askedEmpty = empty.Count(p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Count > 0);

        Assert.True(
            askedHolders > 0 && askedEmpty == 0,
            $"Asked {askedEmpty} peer(s) holding no metadata and {askedHolders} that do. A peer that has " +
            "not declared a size cannot answer, and a magnet has nothing else to wait on.");
    }

    /// <summary>
    /// When nobody has declared a size there is still nothing to lose by asking, so the preference must
    /// not become a refusal - that would stall a magnet whose peers simply never sent the field.
    /// </summary>
    [Fact]
    public void PeersAreStillAskedWhenNobodyDeclaredASize()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();

        var peers = Enumerable.Range(0, 4).Select(_ => MakePeer(metadataSize: null)).ToList();
        foreach (var peer in peers)
        {
            InjectActivePeer(download, peer);
        }

        download.InitializeMetadataBuffer(PieceSize);

        Assert.Contains(peers, p => ((MockUtMetadata)p.UtMetadata).RequestedPieces.Contains(0));
    }

    /// <summary>
    /// The single-peer case must still retry that peer, or a torrent with one metadata source stalls
    /// permanently rather than merely slowly.
    /// </summary>
    [Fact]
    public void ASoleMetadataPeerIsStillRetried()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Transfer.MetadataRequestPipeline = 1;

        var download = new MetadataDownload(torrent);
        download.Start();
        download.InitializeMetadataBuffer(PieceSize);

        var only = MakePeer();
        InjectActivePeer(download, only);

        StalePendingRequest(download, piece: 0, peer: only);
        download.Update();

        Assert.Contains(0, ((MockUtMetadata)only.UtMetadata).RequestedPieces);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    private static void InjectActivePeer(MetadataDownload download, IPeerCommunication peer)
    {
        var field = typeof(MetadataDownload)
            .GetField("_activePeers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<IPeerCommunication>)field.GetValue(download)!).Add(peer);
    }

    private static System.Collections.IDictionary Pending(MetadataDownload download)
    {
        var field = typeof(MetadataDownload)
            .GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (System.Collections.IDictionary)field.GetValue(download)!;
    }

    /// <summary>Replaces the pending request with one old enough to have timed out, keeping its count.</summary>
    private static void StalePendingRequest(MetadataDownload download, int piece, IPeerCommunication peer)
    {
        var dict = Pending(download);
        int attempts = dict.Contains(piece) ? ReadAttempts(dict[piece]!) : 1;

        var pendingType = typeof(MetadataDownload).GetNestedType("PendingMetadataRequest", BindingFlags.NonPublic)!;
        dict[piece] = pendingType.GetConstructors()[0]
            .Invoke([peer, DateTimeOffset.UtcNow.AddMinutes(-5), attempts]);
    }

    private static int CurrentAttempts(MetadataDownload download, int piece)
    {
        var dict = Pending(download);
        return dict.Contains(piece) ? ReadAttempts(dict[piece]!) : 0;
    }

    private static IPeerCommunication? PendingPeer(MetadataDownload download, int piece)
    {
        var dict = Pending(download);
        if (!dict.Contains(piece))
        {
            return null;
        }

        var value = dict[piece]!;
        return (IPeerCommunication?)value.GetType().GetProperty("Peer")!.GetValue(value);
    }

    private static int ReadAttempts(object pending)
        => (int)pending.GetType().GetProperty("Attempts")!.GetValue(pending)!;

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
}
