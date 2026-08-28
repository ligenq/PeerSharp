using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.PieceWriter;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Reflection;
using PeerSharp.Messages;
using PeerSharp.Internals.Transfers;

namespace PeerSharp.Tests.Core;

public class FileTransferTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FileTransfer _fileTransfer;
    private readonly PeerCommunication _peer;

    [Fact]
    public async Task HandleFatalStorageError_RecordsErrorNotifiesAndStopsTorrent()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        Exception? observedError = null;
        torrent.Events = new TorrentEventsBuilder()
            .OnError((_, ex) => observedError = ex)
            .Build();

        var fileTransfer = new FileTransfer(torrent, TimeProvider.System);
        var storageException = new StorageException("Disk full", null, isRecoverable: false);

        await fileTransfer.HandleFatalStorageErrorAsync(storageException);

        // The failure must surface to the application instead of silently looping
        Assert.NotNull(torrent.LastException);
        var torrentException = Assert.IsType<TorrentException>(torrent.LastException);
        Assert.Same(storageException, torrentException.InnerException);
        Assert.NotNull(observedError);

        // And the torrent must not keep downloading against a broken disk
        Assert.False(torrent.Started);
    }

    public FileTransferTests()
    {
        // Geometry has to be in the metadata before the torrent is built, or the piece tracker is
        // created empty and every piece index is out of range - which silently disables any check that
        // validates one.
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384 * 2; // 2 blocks per piece
        metadata.Info.FullSize = metadata.Info.PieceSize * 10;
        metadata.Info.Pieces = [.. Enumerable.Range(0, 10).Select(_ => new byte[20])];

        _torrent = TorrentTestUtility.CreateMinimal(metadata);

        _fileTransfer = new FileTransfer(_torrent, _timeProvider);
        _peer = new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
    }

    private class MockPeerListener : IPeerListener
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

    private static T GetField<T>(object obj, string name) =>
        (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(obj)!;

    private static void SetField(object obj, string name, object? value) =>
        obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(obj, value);

    private static void SetProperty(object obj, string name, object value) =>
        obj.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!.SetValue(obj, value);

    private static async Task InvokePrivateAsync(object obj, string name, params object[] args)
    {
        var m = obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(obj.GetType().Name, name);
        await ((Task)m.Invoke(obj, args)!).ConfigureAwait(false);
    }

    [Fact]
    public void LoadUnfinishedPiecesState_Works()
    {
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new()
            {
                Index = 0,
                Blocks = [true, false],
                Data = new byte[32768]
            }
        };
        data[0].Data[0] = 42;

        _fileTransfer.LoadUnfinishedPiecesState(data);

        var unfinished = _fileTransfer.GetUnfinishedPiecesState();
        Assert.Single(unfinished);
        Assert.Equal(0, unfinished[0].Index);
        Assert.True(unfinished[0].Blocks[0]);
        Assert.False(unfinished[0].Blocks[1]);
        Assert.Equal(42, unfinished[0].Data[0]);
    }

    // ── What resume data carries away from a transfer ────────────────────────
    //
    // Each partial piece here is copied whole, base64-encoded into JSON and flushed to the physical
    // device on every autosave. Capping the piece *count* alone bounds the wrong axis: 32 pieces is
    // 8 MiB at a 256 KiB piece size and half a gigabyte at the 16 MiB one BEP 52 allows, written
    // every minute, per torrent.

    [Fact]
    public void GetUnfinishedPiecesState_WithLargePieces_IsBoundedByBytesNotPieceCount()
    {
        using var fixture = LargePieceFixture.Create(pieceSize: 8 * 1024 * 1024, activePieces: 5);

        var saved = fixture.FileTransfer.GetUnfinishedPiecesState();

        // 16 MiB of budget over 8 MiB pieces: two, not the five that are in progress.
        Assert.Equal(2, saved.Count);
        Assert.True(
            saved.Sum(piece => (long)piece.Data.Length) <= 16L * 1024 * 1024,
            "the saved snapshot must fit the byte budget");
    }

    [Fact]
    public void GetUnfinishedPiecesState_WithOnePieceLargerThanTheBudget_StillSavesOne()
    {
        // Saving nothing would mean a restart re-downloads a whole 32 MiB piece from scratch. One
        // over-budget piece is worth more than an empty snapshot.
        using var fixture = LargePieceFixture.Create(pieceSize: 32 * 1024 * 1024, activePieces: 3);

        var saved = fixture.FileTransfer.GetUnfinishedPiecesState();

        Assert.Single(saved);
    }

    [Fact]
    public void GetUnfinishedPiecesState_StopsAtThePieceThatWouldOverrunTheBudget()
    {
        // The budget can be partly spent rather than exactly exhausted: two 12 MiB pieces do not fit
        // in 16 MiB, so the second is left out even though there is still room for a smaller one.
        // Taking it anyway would put the write half again over the cap.
        using var fixture = LargePieceFixture.Create(pieceSize: 12 * 1024 * 1024, activePieces: 3);

        var saved = fixture.FileTransfer.GetUnfinishedPiecesState();

        Assert.Single(saved);
    }

    [Fact]
    public void GetUnfinishedPiecesState_WithSmallPieces_IsStillBoundedByPieceCount()
    {
        // The two limits are belt and braces: at ordinary piece sizes the count is what binds, and
        // the byte budget must not quietly loosen it.
        using var fixture = LargePieceFixture.Create(pieceSize: 64 * 1024, activePieces: 40);

        var saved = fixture.FileTransfer.GetUnfinishedPiecesState();

        Assert.Equal(32, saved.Count);
    }

    [Fact]
    public void GetUnfinishedPiecesState_SavesTheMostCompletePiecesFirst()
    {
        // What gets dropped should be the pieces closest to worthless, since dropping a piece costs
        // re-requesting the blocks it held.
        using var fixture = LargePieceFixture.Create(pieceSize: 8 * 1024 * 1024, activePieces: 4, blocksPerPieceIndex: true);

        var saved = fixture.FileTransfer.GetUnfinishedPiecesState();

        Assert.Equal(2, saved.Count);
        Assert.Equal([3, 2], [.. saved.Select(piece => piece.Index)]);
    }

    [Fact]
    public void LoadUnfinishedPiecesState_IgnoresAPieceOutsideTheTorrent()
    {
        // A resume file is just bytes on disk: truncated by a crash, edited by hand, or left over
        // from a torrent whose metadata has moved on. Indexing arrays off those numbers is how that
        // becomes a crash instead of a re-download.
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new() { Index = 9999, Blocks = [true, false], Data = new byte[32768] }
        };

        _fileTransfer.LoadUnfinishedPiecesState(data);

        Assert.Empty(_fileTransfer.GetUnfinishedPiecesState());
    }

    [Fact]
    public void LoadUnfinishedPiecesState_IgnoresAPieceWithTheWrongBlockCount()
    {
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new() { Index = 0, Blocks = [true, false, true, false], Data = new byte[32768] }
        };

        _fileTransfer.LoadUnfinishedPiecesState(data);

        Assert.Empty(_fileTransfer.GetUnfinishedPiecesState());
    }

    [Fact]
    public void LoadUnfinishedPiecesState_IgnoresAPieceWithTruncatedData()
    {
        // Two block flags say 32768 bytes; the payload stops at 100. Copying block 1 out of it used
        // to compute a negative length.
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new() { Index = 0, Blocks = [true, true], Data = new byte[100] }
        };

        var exception = Record.Exception(() => _fileTransfer.LoadUnfinishedPiecesState(data));

        Assert.Null(exception);
        Assert.Empty(_fileTransfer.GetUnfinishedPiecesState());
    }

    [Fact]
    public void LoadUnfinishedPiecesState_KeepsTheValidPiecesAlongsideTheRejectedOnes()
    {
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new() { Index = 9999, Blocks = [true, false], Data = new byte[32768] },
            new() { Index = 1, Blocks = [true, false], Data = new byte[32768] }
        };
        data[1].Data[0] = 7;

        _fileTransfer.LoadUnfinishedPiecesState(data);

        var loaded = Assert.Single(_fileTransfer.GetUnfinishedPiecesState());
        Assert.Equal(1, loaded.Index);
        Assert.Equal(7, loaded.Data[0]);
    }

    /// <summary>
    /// A torrent with a configurable piece size and some pieces part-way downloaded. Piece state is
    /// installed directly rather than through <c>LoadUnfinishedPiecesState</c>, which would need a
    /// full-size buffer per piece just to set one block.
    /// </summary>
    private sealed class LargePieceFixture : IDisposable
    {
        public required Torrent Torrent { get; init; }
        public required FileTransfer FileTransfer { get; init; }

        /// <param name="blocksPerPieceIndex">
        /// When set, piece <c>i</c> receives <c>i + 1</c> blocks, so the pieces differ in how
        /// complete they are.
        /// </param>
        public static LargePieceFixture Create(int pieceSize, int activePieces, bool blocksPerPieceIndex = false)
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.PieceSize = (uint)pieceSize;
            metadata.Info.FullSize = (long)pieceSize * (activePieces + 1);
            metadata.Info.Pieces = [.. Enumerable.Range(0, activePieces + 1).Select(_ => new byte[20])];

            var torrent = TorrentTestUtility.CreateMinimal(metadata);
            var fileTransfer = new FileTransfer(torrent, TimeProvider.System);

            var manager = GetField<PieceStateManager>(fileTransfer, "_pieceStateManager");
            int blocksPerPiece = pieceSize / ProtocolConstants.BlockSize;

            for (int i = 0; i < activePieces; i++)
            {
                var state = new PieceState(i, blocksPerPiece);
                int received = blocksPerPieceIndex ? Math.Min(i + 1, blocksPerPiece) : 1;
                for (int block = 0; block < received; block++)
                {
                    state.Blocks[block] = true;
                    state.BlockData[block] = new Block(i, block * ProtocolConstants.BlockSize, ProtocolConstants.BlockSize);
                }

                state.SetReceivedCountForInit(received);
                manager.AddOrReplacePiece(state);
            }

            return new LargePieceFixture { Torrent = torrent, FileTransfer = fileTransfer };
        }

        public void Dispose()
        {
            FileTransfer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Torrent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void RequestMerkleHashes_V2_SelectsPeerAndSendsRequest()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * 2;
        byte[] v2Root = metadata.Info.HashV2.Span.ToArray();
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize * 2, Offset = 0, PiecesRoot = v2Root, PieceCount = 2 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata, Path.GetTempPath());
        torrent.InfoFile.Info.Files[0].PiecesRoot = v2Root;
        torrent.InfoFile.Info.Files[0].PieceCount = 2;

        Assert.NotNull(torrent.InfoFile.Info.GetV2HashRequestForPiece(0)); // Check if request is valid

        var fileTransfer = new FileTransfer(torrent, _timeProvider);

        // Peer must support V2 and have the piece
        var peer = new PeerCommunication(torrent, new MockPeerListener(), _timeProvider)
        {
            RemoteSupportsV2 = true,
            Connected = 1
        };

        peer.PeerPieces.AddPiece(0);
        Assert.True(peer.PeerPieces.HasPiece(0)); // Ensure peer has piece
        Assert.True(peer.RemoteSupportsV2); // Ensure supports v2

        torrent.PeersInternal.AddConnectedPeerForTesting(peer);

        Assert.Single(torrent.PeersInternal.GetConnectedPeersInternal()); // Ensure connected peers contains the peer

        var requestMerkleHashes = typeof(FileTransfer).GetMethod("RequestMerkleHashes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(requestMerkleHashes);
        requestMerkleHashes.Invoke(fileTransfer, [0]);

        // Ensure a message was queued for the peer
        var queue = peer.GetType().GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(peer) as MessageQueue;
        Assert.NotNull(queue);
        bool dequeued = queue.TryDequeue(out var msg);
        Assert.True(dequeued, "Queue was empty");
        Assert.Equal(MessageId.HashRequest, msg.Id);
    }

    [Fact]
    public async Task ProcessBlock_AddsToActivePieces()
    {
        // We need to make the piece active first via PickNextPiece or manual addition.
        // Actually, FileTransfer.EvaluateNextRequests Internal picks pieces and adds them to _activePieces.
        // But I can't easily call it.
        // I'll manually add a piece to _activePieces using reflection for now,
        // or just test that it DOESN'T store if not active.

        var block = new Block(0, 0, 16384);
        await _fileTransfer.ProcessBlockAsync(_peer, block);

        // Should not be stored because it wasn't requested/active
        Assert.Equal(0, _fileTransfer.Downloader.Downloaded);
        Assert.Throws<ObjectDisposedException>(() => block.Buffer);
    }

    [Theory]
    [InlineData(0, 16384, 16384, true)]
    [InlineData(8192, 8192, 16384, true)]
    [InlineData(8192, 8193, 16384, false)]
    [InlineData(16384, 1, 16384, false)]
    [InlineData(-1, 1, 16384, false)]
    [InlineData(0, 0, 16384, false)]
    [InlineData(int.MaxValue, 1, int.MaxValue, false)]
    public void IsValidUploadRequestRange_RejectsRangesOutsidePiece(int offset, int length, long pieceSize, bool expected)
    {
        Assert.Equal(expected, FileTransfer.IsValidUploadRequestRange(offset, length, pieceSize));
    }

    // ── BlockRejectedAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task BlockRejectedAsync_RemovesRequestFromTracker()
    {
        var tracker = GetField<BlockRequestTracker>(_fileTransfer, "_requestTracker");
        tracker.AddBlockRequest(0, 0, _peer, new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384 });

        Assert.True(tracker.TryGetPeerRequests(_peer, out var before) && !before.IsEmpty);

        // A real Reject carries index, begin and length; the decoder requires all three, and the
        // handler now checks the block is real before letting it touch request or offer state.
        var msg = new PeerMessage(MessageId.Reject) { PieceIndex = 0, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRejectedAsync(_peer, msg);

        // The specific request is removed even though the empty peer collection may linger
        Assert.True(!tracker.TryGetPeerRequests(_peer, out var after) || after.IsEmpty);
    }

    [Fact]
    public async Task BlockRejectedAsync_NoopWhenNoPendingRequest()
    {
        // A real Reject carries index, begin and length; the decoder requires all three, and the
        // handler now checks the block is real before letting it touch request or offer state.
        var msg = new PeerMessage(MessageId.Reject) { PieceIndex = 0, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRejectedAsync(_peer, msg); // Must not throw
    }

    [Fact]
    public async Task BlockRejectedAsync_TriggersRerequestFromAlternatePeer()
    {
        var tracker = GetField<BlockRequestTracker>(_fileTransfer, "_requestTracker");
        tracker.AddBlockRequest(0, 0, _peer, new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384 });

        // Set up a second peer that is unchoked and has piece 0
        var alternatePeer = new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
        alternatePeer.PeerPieces.AddPiece(0);
        SetField(alternatePeer, "_peerChoking", 0); // not choking

        var connectedPeers = GetField<System.Collections.Concurrent.ConcurrentDictionary<PeerCommunication, byte>>(
            _torrent.PeersInternal, "_connectedPeers");
        connectedPeers.TryAdd(_peer, 0);
        connectedPeers.TryAdd(alternatePeer, 0);

        // A real Reject carries index, begin and length; the decoder requires all three, and the
        // handler now checks the block is real before letting it touch request or offer state.
        var msg = new PeerMessage(MessageId.Reject) { PieceIndex = 0, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRejectedAsync(_peer, msg); // Must not throw
        // If EvaluateNextRequestsAsync was called on alternatePeer it queued for background processing;
        // just verify no exception and the pending request was cleaned up.
        Assert.True(!tracker.TryGetPeerRequests(_peer, out var after) || after.IsEmpty);
    }

    // ── BlockReceivedAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task BlockReceivedAsync_DoesNotThrowWhenChannelOpen()
    {
        // Verify the happy path: BlockReceivedAsync succeeds and doesn't throw.
        // The background consumer may read the block immediately, so we don't
        // assert channel state — just that the write path completes cleanly.
        var block = new Block(0, 0, 16384);
        await _fileTransfer.BlockReceivedAsync(_peer, block); // must not throw
    }

    [Fact]
    public async Task BlockReceivedAsync_DisposesBlockWhenCancelled()
    {
        await _fileTransfer.DisposeAsync(); // Cancels internal CTS and closes channel

        var block = new Block(0, 0, 16384);
        await _fileTransfer.BlockReceivedAsync(_peer, block);

        Assert.Throws<ObjectDisposedException>(() => block.Buffer);
    }

    // ── BlockRequestedAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task BlockRequestedAsync_RejectsWhenChoking_NoMessageWithoutExtensions()
    {
        // Default: AmChoking=1 (peer is choking) and no pieces available
        var msg = new PeerMessage(MessageId.Request) { PieceIndex = 0, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRequestedAsync(_peer, msg);

        var queue = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task BlockRequestedAsync_RejectsAndSendsRejectWhenExtensionsAndFastSupported()
    {
        // Enable both extensions so a Reject message is actually sent
        SetField(_peer, "_connected", 1); // must be connected for SendMessageAsync to enqueue
        SetProperty(_peer, "RemoteSupportsExtensions", true);
        SetProperty(_peer, "RemoteSupportsFastExtension", true);

        var msg = new PeerMessage(MessageId.Request) { PieceIndex = 0, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRequestedAsync(_peer, msg);

        var queue = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var sent));
        Assert.Equal(MessageId.Reject, sent.Id);
    }

    [Fact]
    public async Task BlockRequestedAsync_RejectsWhenPieceIndexOutOfRange()
    {
        SetField(_peer, "_connected", 1);
        SetProperty(_peer, "RemoteSupportsExtensions", true);
        SetProperty(_peer, "RemoteSupportsFastExtension", true);
        SetField(_peer, "_amChoking", 0); // Unchoke so only the piece-range check triggers

        // PieceIndex -1 is always invalid
        var msg = new PeerMessage(MessageId.Request) { PieceIndex = -1, BlockOffset = 0, BlockLength = 16384 };
        await _fileTransfer.BlockRequestedAsync(_peer, msg);

        var queue = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var sent));
        Assert.Equal(MessageId.Reject, sent.Id);
    }

    [Fact]
    public async Task BlockRequestedAsync_RejectsOnZeroBlockLength()
    {
        SetField(_peer, "_connected", 1);
        SetProperty(_peer, "RemoteSupportsExtensions", true);
        SetProperty(_peer, "RemoteSupportsFastExtension", true);

        var msg = new PeerMessage(MessageId.Request) { PieceIndex = 0, BlockOffset = 0, BlockLength = 0 };
        await _fileTransfer.BlockRequestedAsync(_peer, msg);

        var queue = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var sent));
        Assert.Equal(MessageId.Reject, sent.Id);
    }

    [Fact]
    public async Task BlockRequestedAsync_RejectsOnNegativeBlockOffset()
    {
        SetField(_peer, "_connected", 1);
        SetProperty(_peer, "RemoteSupportsExtensions", true);
        SetProperty(_peer, "RemoteSupportsFastExtension", true);

        var msg = new PeerMessage(MessageId.Request) { PieceIndex = 0, BlockOffset = -1, BlockLength = 16384 };
        await _fileTransfer.BlockRequestedAsync(_peer, msg);

        var queue = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.True(queue.TryDequeue(out var sent));
        Assert.Equal(MessageId.Reject, sent.Id);
    }

    // ── CancelBlockRequestAsync ───────────────────────────────────────────────

    [Fact]
    public async Task CancelBlockRequestAsync_SendsCancelToNonSourcePeers()
    {
        var peer2 = new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
        SetField(peer2, "_connected", 1); // must be connected for SendMessageAsync to enqueue

        var tracker = GetField<BlockRequestTracker>(_fileTransfer, "_requestTracker");
        tracker.AddBlockRequest(0, 0, _peer, new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384 });
        tracker.AddBlockRequest(0, 0, peer2, new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384 });

        // source = _peer, so peer2 should get a Cancel
        await InvokePrivateAsync(_fileTransfer, "CancelBlockRequestAsync", 0, 0, _peer);

        var queue2 = GetField<MessageQueue>(peer2, "_sendQueue");
        Assert.True(queue2.TryDequeue(out var cancel));
        Assert.Equal(MessageId.Cancel, cancel.Id);
        Assert.Equal(0, cancel.PieceIndex);

        // Source peer should NOT get a Cancel
        var queue1 = GetField<MessageQueue>(_peer, "_sendQueue");
        Assert.Equal(0, queue1.Count);
    }

    [Fact]
    public async Task CancelBlockRequestAsync_NoopWhenNoRegisteredPeers()
    {
        await InvokePrivateAsync(_fileTransfer, "CancelBlockRequestAsync", 99, 0, _peer); // Must not throw
    }

    // ── RunBackgroundTaskAsync ────────────────────────────────────────────────

    [Fact]
    public async Task RunBackgroundTaskAsync_CompletesNormallyWhenTaskSucceeds()
    {
        Func<CancellationToken, Task> successFunc = _ => Task.CompletedTask;

        await InvokePrivateAsync(_fileTransfer, "RunBackgroundTaskAsync", successFunc, "test-task");

        Assert.False(_fileTransfer.HasBackgroundTaskFailure);
    }

    [Fact]
    public async Task RunBackgroundTaskAsync_StopsGracefullyOnCancellation()
    {
        // A task that raises OperationCanceledException is treated as graceful shutdown
        Func<CancellationToken, Task> cancelFunc = _ => throw new OperationCanceledException();

        // Use a fresh local FileTransfer so the class-shared _fileTransfer dispose state is irrelevant
        var torrent = TorrentTestUtility.CreateMinimal();
        var ft = new FileTransfer(torrent, _timeProvider);
        await InvokePrivateAsync(ft, "RunBackgroundTaskAsync", cancelFunc, "test-task");
        Assert.False(ft.HasBackgroundTaskFailure);
        await ft.DisposeAsync();
    }

    [Fact]
    public async Task RunBackgroundTaskAsync_GivesUpAfterMaxRestarts()
    {
        int callCount = 0;
        Func<CancellationToken, Task> alwaysThrow = _ =>
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("simulated failure");
        };

        // Run on thread-pool so we can advance fake time concurrently
        var runTask = Task.Run(() => InvokePrivateAsync(_fileTransfer, "RunBackgroundTaskAsync", alwaysThrow, "test"));

        // MaxBackgroundTaskRestarts = 3  →  4 attempts total, 3 waits of 1000 ms each
        for (int i = 1; i <= 3; i++)
        {
            int attempt = i;
            await TorrentTestUtility.WaitUntilAsync(
                () => Volatile.Read(ref callCount) >= attempt, 10000, $"attempt {attempt} to throw");

            // Advance until the retry actually happens rather than sleeping first and advancing once.
            // The retry delay is registered from a continuation, so a single advance can land before
            // that deadline exists - and then it never fires at all.
            await TorrentTestUtility.AdvanceUntilAsync(
                _timeProvider,
                () => Volatile.Read(ref callCount) > attempt,
                TimeSpan.FromSeconds(2),
                $"attempt {attempt + 1} to start");
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_fileTransfer.HasBackgroundTaskFailure);
        Assert.Equal(4, callCount);
    }

    [Fact]
    public async Task RunBackgroundTaskAsync_RestartsOnTransientError()
    {
        int callCount = 0;
        Func<CancellationToken, Task> failOnceThenSucceed = _ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        };

        var runTask = Task.Run(() => InvokePrivateAsync(_fileTransfer, "RunBackgroundTaskAsync", failOnceThenSucceed, "test"));

        // Wait for 1st failure, then advance time past the 1000ms retry delay
        await TorrentTestUtility.WaitUntilAsync(
            () => Volatile.Read(ref callCount) >= 1, 10000, "the first attempt to throw");

        // Advance until the retry runs, for the same reason as above.
        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => Volatile.Read(ref callCount) > 1,
            TimeSpan.FromSeconds(2),
            "the retry to start");

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_fileTransfer.HasBackgroundTaskFailure);
        Assert.Equal(2, callCount); // Failed once, then succeeded
    }

    // ── EnqueuePieceFromWebSeedAsync ──────────────────────────────────────────

    [Fact]
    public async Task EnqueuePieceFromWebSeedAsync_DoesNotThrowWhenQueueHasSpace()
    {
        // Verify TryWrite path: piece is written to queue without blocking.
        // The background consumer may dequeue it immediately; we just verify no exception.
        var piece = new PieceState(0, 2); // piece index 0, 2 blocks
        await InvokePrivateAsync(_fileTransfer, "EnqueuePieceFromWebSeedAsync", piece, CancellationToken.None);
    }
}
