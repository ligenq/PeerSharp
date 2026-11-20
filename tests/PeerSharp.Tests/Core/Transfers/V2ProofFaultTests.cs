using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Transfers;

public sealed class V2ProofFaultTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private static readonly MethodInfo ProcessMessage = typeof(PeerCommunication).GetMethod("ProcessMessageAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ConnectedField = typeof(PeerCommunication).GetField("_connected",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PieceStatesField = typeof(FileTransfer).GetField("_pieceStateManager",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AWaitingPieceSurvivesHashRejectAndPeerDisconnect(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpV2Proof-");
        try
        {
            await RunInDirectoryAsync(root.FullName, seed, scenario, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string path, int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        byte[] payload = new byte[PieceSize * 2];
        scenario.Random.NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("proof.bin").WithVersion(TorrentFileVersion.V2)
            .WithPieceLength(PieceSize).AddFile("proof.bin", payload).Build();
        var info = file.Metadata.Info;
        var entry = Assert.Single(info.Files);
        int piece = seed % 2;
        byte[] piecesRoot = entry.PiecesRoot!;
        byte[] hashes = info.GetV2Hashes(piecesRoot, baseLayer: 0, index: 0, length: 2, proofLayers: 0)!;
        Assert.NotNull(hashes);
        entry.PieceLayers = null;
        entry.PieceLayerHashes.Clear();
        Assert.Null(info.GetV2ExpectedPieceHash(piece));

        var clock = new FakeTimeProvider();
        await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, path, timeProvider: clock);
        await torrent.StartAsync(ct);
        var transfer = torrent.FileTransferInternal;
        var states = (PieceStateManager)PieceStatesField.GetValue(transfer)!;
        var peers = Enumerable.Range(0, 3).Select(i => new ProofPeer(i, torrent, clock)).ToArray();
        scenario.Random.Shuffle(peers);
        foreach (var peer in peers)
        {
            ConnectedField.SetValue(peer, 1);
            peer.PeerPieces.AddPiece(piece);
            torrent.PeersInternal.AddConnectedPeerForTesting(peer);
        }

        scenario.Record($"send payload for piece {piece} once, with no piece layer available");
        var block = new Block(piece, 0, PieceSize);
        payload.AsSpan(piece * PieceSize, PieceSize).CopyTo(block.Buffer);
        await transfer.WebSeedBlockReceivedAsync(block);

        var asked = new HashSet<ProofPeer>();
        byte[] invalidHashes = (byte[])hashes.Clone();
        invalidHashes[0] ^= 0xff;
        for (int attempt = 0; attempt < peers.Length; attempt++)
        {
            await TorrentTestUtility.WaitUntilAsync(
                () => peers.Sum(peer => peer.RequestCount) == attempt + 1
                    && states.TryGetPiece(piece, out var state) && !state.IsWriting,
                because: $"proof request {attempt + 1} and release of the piece worker");

            var peer = Assert.Single(peers, peer => peer.RequestCount > 0 && !asked.Contains(peer));
            asked.Add(peer);
            var request = Assert.IsType<V2HashRequest>(peer.LastRequest);
            Assert.Equal((0, 0, 2, 0), (request.BaseLayer, request.Index, request.Length, request.ProofLayers));
            Assert.Equal(piecesRoot, request.PiecesRoot);
            AssertWaiting(torrent, transfer, piece);

            if (attempt == 0)
            {
                scenario.Record($"peer {peer.Id} rejects the request and stays connected");
                await ReplyAsync(peer, request, MessageId.HashReject, []);
            }
            else if (attempt == 1)
            {
                scenario.Record($"peer {peer.Id} disconnects before replying");
                await peer.CloseAsync();
            }
            else
            {
                scenario.Record($"peer {peer.Id} supplies invalid hashes, then a valid proof");
                await ReplyAsync(peer, request, MessageId.Hashes, invalidHashes);
                Assert.Null(info.GetV2ExpectedPieceHash(piece));
                AssertWaiting(torrent, transfer, piece);
                if ((seed & 2) != 0)
                {
                    scenario.Record("retry once more before delivering the valid proof");
                    clock.Advance(TimeSpan.FromSeconds(3));
                    transfer.Update();
                    await TorrentTestUtility.WaitUntilAsync(
                        () => peers.Sum(peer => peer.RequestCount) == 4
                            && states.TryGetPiece(piece, out var state) && !state.IsWriting,
                        because: "the additional retry to finish");
                    AssertWaiting(torrent, transfer, piece);
                }
                await ReplyAsync(peer, request, MessageId.Hashes, hashes);
                Assert.NotNull(info.GetV2ExpectedPieceHash(piece));
            }

            clock.Advance(TimeSpan.FromSeconds(3));
            transfer.Update();
        }

        await TorrentTestUtility.WaitUntilAsync(() => torrent.Pieces.HasPiece(piece), because: "the retained piece to verify");
        Assert.Equal(0, transfer.GetUnfinishedBytes());
        Assert.Equal(PieceSize, transfer.Downloader.Downloaded);
        await torrent.StopAsync(ct);
        byte[] disk = await File.ReadAllBytesAsync(Path.Combine(path, torrent.GetFileInfo(0).Path), ct);
        Assert.True(payload.AsSpan(piece * PieceSize, PieceSize).SequenceEqual(disk.AsSpan(piece * PieceSize, PieceSize)));
    }

    private static void AssertWaiting(Torrent torrent, FileTransfer transfer, int piece)
    {
        Assert.False(torrent.Pieces.HasPiece(piece));
        Assert.Equal(PieceSize, transfer.GetUnfinishedBytes());
    }

    private static async Task ReplyAsync(ProofPeer peer, V2HashRequest request, MessageId id, byte[] data)
    {
        using var message = new PeerMessage(id)
        {
            HashPiecesRoot = request.PiecesRoot,
            HashBaseLayer = request.BaseLayer,
            HashIndex = request.Index,
            HashLength = request.Length,
            HashProofLayers = request.ProofLayers,
            Data = data
        };
        await (Task)ProcessMessage.Invoke(peer, [message])!;
    }

    private sealed class ProofPeer : PeerCommunication
    {
        private int _requests;

        public ProofPeer(int id, Torrent torrent, TimeProvider clock)
            : base(torrent, torrent.PeersInternal, clock)
        {
            Id = id;
            RemoteSupportsV2 = true;
        }

        public int Id { get; }
        public int RequestCount => Volatile.Read(ref _requests);
        public V2HashRequest? LastRequest { get; private set; }

        public override Task SendMessageAsync(PeerMessage msg)
        {
            using (msg)
            {
                if (msg.Id == MessageId.HashRequest)
                {
                    LastRequest = new V2HashRequest(msg.HashPiecesRoot!, msg.HashBaseLayer, msg.HashIndex, msg.HashLength, msg.HashProofLayers);
                    Interlocked.Increment(ref _requests);
                }
            }
            return Task.CompletedTask;
        }
    }
}
