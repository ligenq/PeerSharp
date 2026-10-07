using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Internals.Utilities;
using PeerSharp.PiecePicking;
using PeerSharp.PieceWriter;
using TorrentFileEntry = PeerSharp.Internals.TorrentFileEntry;
using TransferStats = PeerSharp.Internals.TransferStats;

namespace PeerSharp.Tests.Core.Transfers;

public class TransferIntegrityRegressionTests
{
    [Fact]
    public async Task ACompleteResumedPieceIsQueuedForVerificationWithoutAnotherBlock()
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        torrent.Settings.Transfer.MaxConcurrentPieceHashing = 1;
        await using var transfer = new FileTransfer(torrent, TimeProvider.System);
        transfer.LoadUnfinishedPiecesState([new() { Index = 0, Blocks = [true], Data = new byte[ProtocolConstants.BlockSize] }]);
        var limiter = (AdjustableConcurrencyLimiter)typeof(FileTransfer).GetField("_hashSemaphore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transfer)!;
        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        typeof(Torrent).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(torrent, 1);
        try
        {
            transfer.Update();
            var manager = (PieceStateManager)typeof(FileTransfer).GetField("_pieceStateManager", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transfer)!;
            Assert.True(manager.TryGetPiece(0, out var piece));
            Assert.True(piece.IsWriting);
            Assert.Equal(1, piece.ReceivedCount);
        }
        finally
        {
            limiter.Release();
            typeof(Torrent).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(torrent, 0);
        }
    }

    [Fact]
    public async Task OversizedOrNullResumeBuffersAreIgnored()
    {
        await using var torrent = CreateTorrent(1000);
        await using var transfer = new FileTransfer(torrent, TimeProvider.System);
        transfer.LoadUnfinishedPiecesState([
            new() { Index = 0, Blocks = [true], Data = new byte[ProtocolConstants.BlockSize] },
            new() { Index = 0, Blocks = null!, Data = new byte[1000] },
            new() { Index = 0, Blocks = [true], Data = null! },
            null!
        ]);
        Assert.Empty(transfer.GetUnfinishedPiecesState());
    }

    [Fact]
    public void ResumeSnapshotsCannotCopyReturnedPoolBuffersDuringAReset()
    {
        using var state = new PieceState(0, 1);
        Parallel.For(0, 500, _ =>
        {
            var block = new Block(0, 0, ProtocolConstants.BlockSize);
            block.Buffer.AsSpan(0, block.Length).Fill(42);
            if (!state.TryAddBlockFromWebSeed(0, block))
            {
                block.Dispose();
            }
            var snapshot = state.CaptureResumeData(ProtocolConstants.BlockSize);
            if (snapshot?.Blocks[0] == true)
            {
                Assert.All(snapshot.Data, value => Assert.Equal(42, value));
            }
            state.Reset();
        });
    }

    [Fact]
    public async Task StaleRemovalCannotEraseANewerRequest()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        var tracker = new BlockRequestTracker();
        var old = new BlockRequest { PieceIndex = 0, Length = ProtocolConstants.BlockSize };
        var retry = new BlockRequest { PieceIndex = 0, Length = ProtocolConstants.BlockSize };
        tracker.AddBlockRequest(0, 0, peer, old);
        tracker.AddBlockRequest(0, 0, peer, retry);

        Assert.False(tracker.TryRemovePeerRequest(peer, (0, 0), out _, old));
        Assert.Equal(1, tracker.GetPendingRequestCount(0, 0));
        Assert.True(tracker.TryGetPeerRequests(peer, out var requests));
        Assert.Same(retry, Assert.Single(requests.Values));

        Assert.True(tracker.TryRemovePeerRequest(peer, (0, 0), out var removed, retry));
        Assert.Same(retry, removed);
        Assert.Equal(0, tracker.BlockRequestIndexCount);
        Assert.True(requests.IsEmpty);
        Assert.Same(requests, tracker.GetOrAddPeerRequests(peer));
    }

    [Fact]
    public async Task ConcurrentAddAndRemovalKeepBothRequestIndexesConsistent()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        var tracker = new BlockRequestTracker();
        var peers = Enumerable.Range(0, 32).Select(_ => new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System)).ToArray();
        Parallel.For(0, peers.Length, i =>
        {
            for (int n = 0; n < 200; n++)
            {
                var request = new BlockRequest { PieceIndex = 0, Length = ProtocolConstants.BlockSize };
                tracker.AddBlockRequest(0, 0, peers[i], request);
                tracker.TryRemovePeerRequest(peers[i], (0, 0), out _, request);
            }
            tracker.AddBlockRequest(0, 0, peers[i], new BlockRequest { PieceIndex = 0, Length = ProtocolConstants.BlockSize });
        });
        Assert.Equal(1, tracker.BlockRequestIndexCount);
        Assert.Equal(peers.Length, tracker.GetPendingRequestCount(0, 0));
        foreach (var peer in peers)
        {
            Assert.True(tracker.TryGetPeerRequests(peer, out var requests));
            Assert.Equal(1, requests.Count);
        }
        Parallel.ForEach(peers, tracker.RemovePeer);
        Assert.Equal(0, tracker.BlockRequestIndexCount);
        Assert.Empty(tracker.EnumeratePeerRequests());
    }

    [Theory]
    [InlineData(-1, 0, 16384)]
    [InlineData(1, 0, 16384)]
    [InlineData(0, -1, 16384)]
    [InlineData(0, 1, 16384)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 16384, 16384)]
    public async Task MalformedWebSeedBlocksAreDisposedWithoutCreatingPieceState(int piece, int offset, int length)
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        using var manager = CreateManager(torrent);
        var processor = CreateProcessor(torrent, manager, new(), _ => Task.CompletedTask);
        var block = new Block(piece, offset, length);
        await processor.HandleWebSeedBlockReceivedAsync(block, TestContext.Current.CancellationToken);
        Assert.True(block.Data.IsEmpty);
        Assert.Equal(0, manager.Count);
    }

    [Fact]
    public async Task LateWebSeedBlockCannotRecreateACompletedPiece()
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        torrent.Pieces.AddPiece(0);
        using var manager = CreateManager(torrent);
        var processor = CreateProcessor(torrent, manager, new(), _ => Task.CompletedTask);
        var block = new Block(0, 0, ProtocolConstants.BlockSize);
        await processor.HandleWebSeedBlockReceivedAsync(block, TestContext.Current.CancellationToken);
        Assert.True(block.Data.IsEmpty);
        Assert.Equal(0, manager.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDuplicateCancellationDoesNotStrandACompletePiece(bool webSeed)
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        using var manager = CreateManager(torrent);
        var tracker = new BlockRequestTracker();
        PieceState? queued = null;
        var processor = CreateProcessor(torrent, manager, tracker, state => { queued = state; return Task.CompletedTask; },
            (_, _, _) => throw new IOException("connection closed during cancel"));
        var block = new Block(0, 0, ProtocolConstants.BlockSize);
        if (webSeed)
        {
            await processor.HandleWebSeedBlockReceivedAsync(block, TestContext.Current.CancellationToken);
        }
        else
        {
            var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
            manager.TryAddPiece(new PieceState(0, 1));
            tracker.AddBlockRequest(0, 0, peer, new BlockRequest { PieceIndex = 0, Length = block.Length });
            await processor.HandlePeerBlockAsync(peer, block);
            Assert.Equal(0, tracker.BlockRequestIndexCount);
        }
        Assert.NotNull(queued);
        Assert.True(queued.IsWriting);
        Assert.Same(block, queued.BlockData[0]);
        Assert.False(block.Data.IsEmpty);
    }

    [Fact]
    public async Task PruningCannotDisposeACompletePieceBeingVerified()
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        using var manager = CreateManager(torrent);
        var piece = new PieceState(0, 1);
        var block = new Block(0, 0, ProtocolConstants.BlockSize);
        piece.TryAddBlockFromWebSeed(0, block);
        Assert.True(piece.TryCompleteAndSetWriting());
        manager.TryAddPiece(piece);
        manager.PruneStalePieces();
        Assert.True(manager.ContainsPiece(0));
        Assert.False(block.Data.IsEmpty);
    }

    [Fact]
    public void DisposedPieceCannotTakeOwnershipOfAnotherBlock()
    {
        var piece = new PieceState(0, 1);
        piece.Dispose();
        using var block = new Block(0, 0, ProtocolConstants.BlockSize);
        Assert.False(piece.TryAddBlockFromWebSeed(0, block));
        Assert.False(piece.TryCompleteAndSetWriting());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VerificationRejectsIncompleteOrMisplacedBlocks(int corruption)
    {
        byte[] data = new byte[ProtocolConstants.BlockSize];
        await using var torrent = CreateTorrent(data.Length);
        torrent.InfoFile.Info.Pieces.Add(SHA1.HashData(data));
        using var piece = new PieceState(0, 1);
        piece.BlockData[0] = corruption switch
        {
            0 => new Block(0, 0, data.Length - 1),
            1 => new Block(0, 1, data.Length),
            _ => new Block(1, 0, data.Length)
        };
        var writer = CreateWriter(torrent, _ => { });
        using var outcome = await writer.VerifyAsync(piece, TestContext.Current.CancellationToken);
        Assert.False(outcome.HashSuccess);
        Assert.True(outcome.HashFailed);
        Assert.Null(outcome.FullData);
    }

    [Fact]
    public async Task MissingV1HashCannotBecomeSuccessfulVerification()
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        using var piece = CompletePiece(new byte[ProtocolConstants.BlockSize]);
        using var outcome = await CreateWriter(torrent, _ => { }).VerifyAsync(piece, TestContext.Current.CancellationToken);
        Assert.False(outcome.HashSuccess);
        Assert.True(outcome.HashFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridVerificationRequiresBothHashesDuringDownloadAndRecheck(bool validV1)
    {
        byte[] data = new byte[ProtocolConstants.BlockSize];
        data[0] = 42;
        var metadata = Metadata(data.Length);
        metadata.Info.Version = TorrentVersion.Hybrid;
        metadata.Info.Files[0].PieceCount = 1;
        metadata.Info.Files[0].PiecesRoot = SHA256.HashData(data);
        metadata.Info.Pieces.Add(validV1 ? SHA1.HashData(data) : new byte[20]);
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        using var piece = CompletePiece(data);
        using var outcome = await CreateWriter(torrent, _ => { }).VerifyAsync(piece, TestContext.Current.CancellationToken);
        Assert.Equal(validV1, outcome.HashSuccess);
        Assert.Equal(!validV1, outcome.HashFailed);
        Assert.Equal(validV1, new TorrentPieceCheckerContext(torrent).VerifyPiece(0, data));
    }

    [Fact]
    public void HybridV1HashIncludesThePaddingAfterAShortFile()
    {
        byte[] data = new byte[1000];
        data[0] = 42;
        var metadata = Metadata(2 * ProtocolConstants.BlockSize);
        metadata.Info.Version = TorrentVersion.Hybrid;
        metadata.Info.Files[0].Size = data.Length;
        metadata.Info.Files[0].PieceCount = 1;
        byte[] padded = new byte[ProtocolConstants.BlockSize];
        data.CopyTo(padded, 0);
        metadata.Info.Pieces.Add(SHA1.HashData(padded));
        Assert.True(metadata.Info.VerifyV1PieceHash(0, data));
        metadata.Info.Pieces[0] = SHA1.HashData(data);
        Assert.False(metadata.Info.VerifyV1PieceHash(0, data));
    }

    [Fact]
    public async Task MissingMerkleProofRetainsBlocksAndRetriesOnlyAfterTheProofArrives()
    {
        byte[] data = new byte[ProtocolConstants.BlockSize];
        byte[] hash = SHA1.HashData(data);
        byte[] sibling = SHA1.HashData(new byte[] { 1 });
        var metadata = Metadata(2 * data.Length);
        metadata.Info.MerkleRootHash = MerkleTreeSha1.HashPair(hash, sibling);
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        using var piece = CompletePiece(data);
        var clock = new FakeTimeProvider();
        await using var transfer = new FileTransfer(torrent, clock);
        var process = typeof(FileTransfer).GetMethod("ProcessSinglePieceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)process.Invoke(transfer, [piece, TestContext.Current.CancellationToken])!;
        Assert.False(torrent.Pieces.HasPiece(0));
        Assert.Equal(1, piece.ReceivedCount);
        Assert.Equal(0, piece.HashFailures);
        Assert.False(piece.IsWriting);
        Assert.False(piece.TryCompleteAndSetWriting(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(piece.TryCompleteAndSetWriting(clock.GetUtcNow()));
        Assert.True(torrent.MerkleTree!.TryAddPieceProof(0, hash, [sibling]));
        using var outcome = await CreateWriter(torrent, _ => { }).VerifyAsync(piece, TestContext.Current.CancellationToken);
        Assert.True(outcome.HashSuccess);
    }

    [Fact]
    public async Task MissingV2ProofRequestsHashesWithoutBlamingTheDataSupplier()
    {
        var metadata = Metadata(2 * ProtocolConstants.BlockSize);
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.Files[0].PiecesRoot = new byte[32];
        metadata.Info.Files[0].PieceCount = 2;
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        using var piece = CompletePiece(new byte[ProtocolConstants.BlockSize]);
        int requests = 0;
        using var outcome = await CreateWriter(torrent, _ => requests++).VerifyAsync(piece, TestContext.Current.CancellationToken);
        Assert.False(outcome.HashSuccess);
        Assert.False(outcome.HashFailed);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancelledWriteIsPropagatedWithoutTurningItIntoADiskFailure()
    {
        await using var torrent = CreateTorrent(ProtocolConstants.BlockSize);
        using var piece = CompletePiece(new byte[ProtocolConstants.BlockSize]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateWriter(torrent, _ => { }).WriteAsync(piece,
            ProtocolConstants.BlockSize, new byte[ProtocolConstants.BlockSize], cancellation.Token));
    }

    private static PieceState CompletePiece(byte[] bytes)
    {
        var state = new PieceState(0, 1);
        var block = new Block(0, 0, bytes.Length);
        bytes.CopyTo(block.Buffer, 0);
        state.TryAddBlockFromWebSeed(0, block);
        state.TryCompleteAndSetWriting();
        return state;
    }

    private static PieceVerificationWriter CreateWriter(Torrent torrent, Action<int> requestHashes)
        => new(torrent, TimeProvider.System, NullLogger<PieceVerificationWriter>.Instance, ProtocolConstants.BlockSize, requestHashes);

    private static TorrentFileMetadata Metadata(int size)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = size;
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "file.bin", Size = size });
        return metadata;
    }

    private static Torrent CreateTorrent(int size) => TorrentTestUtility.CreateMinimal(Metadata(size));

    private static PieceStateManager CreateManager(Torrent torrent)
        => new(new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared),
            NullLogger<PieceStateManager>.Instance, () => 1);

    private static BlockProcessor CreateProcessor(Torrent torrent, PieceStateManager manager, BlockRequestTracker tracker,
        Func<PieceState, Task> enqueue, Func<int, int, PeerCommunication?, Task>? cancel = null)
        => new(new BlockProcessorOptions
        {
            PieceStateManager = manager,
            BlockSize = ProtocolConstants.BlockSize,
            EnqueuePeerPiece = enqueue,
            EnqueueWebSeedPiece = (state, _) => enqueue(state),
            Downloader = new TransferStats(),
            RequestCompletionTracker = new(tracker, TimeProvider.System),
            Torrent = torrent,
            CancelBlockRequest = cancel ?? ((_, _, _) => Task.CompletedTask),
            Logger = NullLogger<BlockProcessor>.Instance
        });
}
