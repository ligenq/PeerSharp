using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Transfers;

public sealed class EndgameHandoffFaultTests
{
    private const int BlockSize = ProtocolConstants.BlockSize;
    private const int PieceSize = 2 * BlockSize;
    private const int LastPiece = 10;
    private static readonly FieldInfo ConnectedField = typeof(PeerCommunication).GetField("_connected",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RequestsField = typeof(FileTransfer).GetField("_requestTracker",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StatesField = typeof(FileTransfer).GetField("_pieceStateManager",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task LateBlocksAndCancelsDoNotInterfereWithEndgameHandoff(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpEndgame-");
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
        byte[] payload = new byte[(LastPiece + 1) * PieceSize];
        scenario.Random.NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("endgame.bin").WithPieceLength(PieceSize)
            .AddFile("endgame.bin", payload).Build();
        var clock = new FakeTimeProvider();
        await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, path, timeProvider: clock);
        string payloadPath = Path.Combine(path, torrent.GetFileInfo(0).Path);
        await File.WriteAllBytesAsync(payloadPath, payload[..(LastPiece * PieceSize)], ct);
        Assert.Equal(LastPiece, await torrent.ForceRecheckAsync(cancellationToken: ct));
        await torrent.StartAsync(ct);
        var transfer = torrent.FileTransferInternal;
        Assert.True(transfer.EndGameMode);
        var tracker = (BlockRequestTracker)RequestsField.GetValue(transfer)!;
        var states = (PieceStateManager)StatesField.GetValue(transfer)!;
        Assert.True(states.TryAddPiece(new PieceState(LastPiece, 2)));

        var cancelEntered = new TaskCompletionSource<PeerCommunication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ObserveCancelAsync(PeerCommunication peer)
            => cancelEntered.TrySetResult(peer) ? releaseCancel.Task : Task.CompletedTask;

        var peers = Enumerable.Range(0, 4).Select(_ => new ScriptedPeer(torrent, clock, ObserveCancelAsync)).ToArray();
        scenario.Random.Shuffle(peers);
        foreach (var peer in peers)
        {
            ConnectedField.SetValue(peer, 1);
            torrent.PeersInternal.AddConnectedPeerForTesting(peer);
            tracker.AddBlockRequest(LastPiece, 0, peer, Request());
        }

        var departed = peers[0];
        var survivor = peers[1];
        int publications = 0;
        torrent.Events = new TorrentEventsBuilder().OnPieceCompleted((_, progress) =>
        {
            if (progress.PieceIndex == LastPiece)
            {
                Interlocked.Increment(ref publications);
            }
        }).Build();

        scenario.Record("disconnect a peer with a duplicate request");
        Assert.Equal(4, tracker.GetPendingRequestCount(LastPiece, 0));
        await departed.CloseAsync();
        Assert.False(tracker.TryGetPeerRequests(departed, out _));
        Assert.Equal(3, tracker.GetPendingRequestCount(LastPiece, 0));

        Block MakeBlock(int offset, bool stale = false)
        {
            var block = new Block(LastPiece, offset, BlockSize);
            payload.AsSpan(LastPiece * PieceSize + offset, BlockSize).CopyTo(block.Buffer);
            if (stale) block.Buffer[0] ^= 0xff;
            return block;
        }

        bool lateDuringCancel = (seed & 2) != 0;
        if (!lateDuringCancel)
        {
            scenario.Record("deliver the disconnected peer's corrupt block before the handoff");
            await transfer.ProcessBlockAsync(departed, MakeBlock(0, stale: true));
            Assert.Equal(0, transfer.Downloader.Downloaded);
        }

        bool webSeedWins = (seed & 1) != 0;
        scenario.Record($"accept the {(webSeedWins ? "web seed's" : "surviving peer's")} block and pause the duplicate cancel pass");
        Task winning = webSeedWins
            ? transfer.WebSeedBlockReceivedAsync(MakeBlock(0))
            : transfer.ProcessBlockAsync(survivor, MakeBlock(0));
        ScriptedPeer replaced;
        BlockRequest replacement;
        var key = (LastPiece, 0);
        try
        {
            var firstCanceled = await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            replaced = peers.Skip(2).First(peer => !ReferenceEquals(peer, firstCanceled));
            Assert.True(tracker.TryGetPeerRequests(replaced, out var pending));
            Assert.True(pending.TryGetValue(key, out var old));
            Assert.True(tracker.TryRemovePeerRequest(replaced, key, out _, old));
            replacement = Request();
            tracker.AddBlockRequest(LastPiece, 0, replaced, replacement);
            scenario.Record("replace a second duplicate after its old request was captured for cancellation");
            if (lateDuringCancel)
            {
                scenario.Record("deliver the disconnected peer's corrupt block while the cancel pass is paused");
                await transfer.ProcessBlockAsync(departed, MakeBlock(0, stale: true));
            }
        }
        finally
        {
            releaseCancel.TrySetResult();
            await winning.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        Assert.True(tracker.TryGetPeerRequests(replaced, out var requests));
        Assert.True(requests.TryGetValue(key, out var current));
        Assert.Same(replacement, current);
        Assert.True(tracker.TryGetBlockPeers(key, out var blockPeers));
        Assert.Same(replacement, Assert.Single(blockPeers).Value);
        Assert.Equal(BlockSize, transfer.Downloader.Downloaded);

        scenario.Record($"finish the piece from the {(webSeedWins ? "surviving peer" : "web seed")}, then deliver late copies");
        if (webSeedWins)
        {
            tracker.AddBlockRequest(LastPiece, BlockSize, survivor, Request(BlockSize));
            await transfer.ProcessBlockAsync(survivor, MakeBlock(BlockSize));
        }
        else
        {
            await transfer.WebSeedBlockReceivedAsync(MakeBlock(BlockSize));
        }
        await Task.WhenAll(peers.Skip(1).Select(peer => peer.HaveSent.Task)).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(torrent.Pieces.HasPiece(LastPiece));
        Assert.False(states.ContainsPiece(LastPiece));
        Assert.Equal(0, tracker.BlockRequestIndexCount);
        Assert.All(tracker.EnumeratePeerRequests().Values, pending => Assert.Empty(pending.Values));

        var latePeer = MakeBlock(0, stale: true);
        var lateWebSeed = MakeBlock(0, stale: true);
        await transfer.ProcessBlockAsync(replaced, latePeer);
        await transfer.WebSeedBlockReceivedAsync(lateWebSeed);
        Assert.True(latePeer.Data.IsEmpty);
        Assert.True(lateWebSeed.Data.IsEmpty);
        Assert.False(states.ContainsPiece(LastPiece));
        await torrent.StopAsync(ct);
        Assert.Equal(1, Volatile.Read(ref publications));
        Assert.All(peers.Skip(1), peer => Assert.Equal(1, peer.HaveCount));
        Assert.Equal(0, departed.HaveCount);
        Assert.Equal(PieceSize, transfer.Downloader.Downloaded);
        Assert.Equal(payload, await File.ReadAllBytesAsync(payloadPath, ct));

        BlockRequest Request(int offset = 0) => new()
        {
            PieceIndex = LastPiece,
            Offset = offset,
            Length = BlockSize,
            Timestamp = clock.GetUtcNow()
        };
    }

    private sealed class ScriptedPeer(Torrent torrent, TimeProvider clock, Func<PeerCommunication, Task> onCancel)
        : PeerCommunication(torrent, torrent.PeersInternal, clock)
    {
        private int _haveCount;

        public int HaveCount => Volatile.Read(ref _haveCount);
        public TaskCompletionSource HaveSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task SendMessageAsync(PeerMessage msg)
        {
            using (msg)
            {
                if (msg.Id == MessageId.Cancel) return onCancel(this);
                if (msg.Id == MessageId.Have && msg.HavePieceIndex == LastPiece)
                {
                    Interlocked.Increment(ref _haveCount);
                    HaveSent.TrySetResult();
                }
            }
            return Task.CompletedTask;
        }
    }
}
