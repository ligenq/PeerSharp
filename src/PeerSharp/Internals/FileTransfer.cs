using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.PiecePicking;
using System.Collections.Concurrent;
using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals;

/*
 * THREAD-SAFETY GUIDELINES FOR THIS FILE:
 *
 * This file uses a hybrid synchronization strategy:
 *
 * 1. Interlocked: For simple atomic counters and flags (e.g., _receivedCount, _isWriting)
 *    - Use when: Single value updates that don't need to be coordinated with other state
 *    - Pattern: Interlocked.Increment/Exchange/CompareExchange
 *
 * 2. Lock (_lock): For compound operations that modify multiple related fields
 *    - Use when: Multiple values must be updated atomically together
 *    - Pattern: lock (_lock) { ... }
 *    - Note: Using C# 13 Lock type for better performance than object locks
 *
 * 3. ConcurrentDictionary: For thread-safe key-value storage with frequent concurrent access
 *    - Use when: Many threads read/write different keys simultaneously
 *    - Note: Individual operations are atomic, but compound operations need external sync
 *
 * 4. Channel<T>: For producer-consumer patterns with backpressure
 *    - Use when: Decoupling producers from consumers with bounded queues
 *
 * KEY INVARIANTS:
 * - PieceState._lock protects: Blocks[], _receivedCount, _isWriting, BlockData additions
 * - Use TryCompleteAndSetWriting() for atomic completion check + write claim
 * - Background tasks use _cts for coordinated cancellation
 */

internal sealed class PieceState : IDisposable
{
    private readonly Lock _lock = new();
    private bool _isWriting;
    private int _receivedCount;
    private bool _disposed;

    public PieceState(int index, int blocksCount)
    {
        Index = index;
        Blocks = new bool[blocksCount];
        BlockData = new Block?[blocksCount];
    }

    public Block?[] BlockData { get; }
    public bool[] Blocks { get; }
    public HashSet<PeerCommunication> Contributors { get; } = new();
    public int Index { get; }

    public bool IsWriting
    {
        get
        {
            lock (_lock)
            {
                return _isWriting;
            }
        }
    }

    public int ReceivedCount
    {
        get
        {
            lock (_lock)
            {
                return _receivedCount;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            for (int i = 0; i < BlockData.Length; i++)
            {
                BlockData[i]?.Dispose();
                BlockData[i] = null;
            }
        }
    }

    /// <summary>
    /// Disposes all blocks and clears state after successful completion.
    /// </summary>
    public void CompleteAndDispose()
    {
        Dispose();
    }

    public long GetReceivedBytes(long pieceStartOffset, uint pieceSize, long torrentFullSize, long rangeStart, long rangeSize)
    {
        lock (_lock)
        {
            if (_receivedCount == 0)
            {
                return 0;
            }

            long rangeEnd = rangeStart + rangeSize;
            long bytes = 0;

            for (int i = 0; i < Blocks.Length; i++)
            {
                if (Blocks[i])
                {
                    long blockStart = pieceStartOffset + (i * (long)ProtocolConstants.BlockSize);
                    long blockEnd = blockStart + ProtocolConstants.BlockSize;

                    // Cap blockEnd to piece end or torrent end
                    long pieceEnd = pieceStartOffset + pieceSize;
                    if (pieceEnd > torrentFullSize)
                    {
                        pieceEnd = torrentFullSize;
                    }

                    if (blockEnd > pieceEnd)
                    {
                        blockEnd = pieceEnd;
                    }

                    long overlapStart = Math.Max(blockStart, rangeStart);
                    long overlapEnd = Math.Min(blockEnd, rangeEnd);

                    if (overlapEnd > overlapStart)
                    {
                        bytes += overlapEnd - overlapStart;
                    }
                }
            }
            return bytes;
        }
    }

    /// <summary>
    /// Resets the piece state for retry. Disposes all blocks under lock.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            // Take snapshot of blocks to dispose
            var blocksToDispose = new List<Block?>(BlockData);
            Array.Clear(BlockData, 0, BlockData.Length);
            Array.Clear(Blocks, 0, Blocks.Length);
            _receivedCount = 0;
            Contributors.Clear();
            _isWriting = false;

            // Dispose outside the critical section for BlockData, but inside _lock
            // to prevent concurrent access during reset
            foreach (var b in blocksToDispose)
            {
                b?.Dispose();
            }
        }
    }

    /// <summary>
    /// Sets the received count during initialization.
    /// </summary>
    public void SetReceivedCountForInit(int count)
    {
        lock (_lock)
        {
            _receivedCount = count;
        }
    }

    /// <summary>
    /// Attempts to add a block. Returns true if added successfully.
    /// Thread-safe: uses lock to ensure atomicity of block add + state update.
    /// </summary>
    public bool TryAddBlock(int blockIdx, Block block, PeerCommunication contributor)
    {
        lock (_lock)
        {
            if (_isWriting)
            {
                return false;
            }

            if (blockIdx >= Blocks.Length)
            {
                return false;
            }

            if (BlockData[blockIdx] != null)
            {
                return false; // Already have this block
            }

            BlockData[blockIdx] = block;
            Blocks[blockIdx] = true;
            _receivedCount++;
            Contributors.Add(contributor);
            return true;
        }
    }

    /// <summary>
    /// BEP 19: Attempts to add a block from a web seed (no PeerCommunication contributor).
    /// Thread-safe: uses lock to ensure atomicity of block add + state update.
    /// </summary>
    public bool TryAddBlockFromWebSeed(int blockIdx, Block block)
    {
        lock (_lock)
        {
            if (_isWriting)
            {
                return false;
            }

            if (blockIdx >= Blocks.Length)
            {
                return false;
            }

            if (BlockData[blockIdx] != null)
            {
                return false; // Already have this block
            }

            BlockData[blockIdx] = block;
            Blocks[blockIdx] = true;
            _receivedCount++;
            // No contributor for web seeds
            return true;
        }
    }

    /// <summary>
    /// Checks if the piece is fully received and marks it as writing.
    /// <summary>
    /// Atomically checks if piece is complete AND sets the writing flag.
    /// Returns true only if the piece is complete AND this call successfully claimed write responsibility.
    /// </summary>
    public bool TryCompleteAndSetWriting()
    {
        lock (_lock)
        {
            if (_receivedCount != Blocks.Length)
            {
                return false;
            }

            if (_isWriting)
            {
                return false;
            }

            _isWriting = true;
            return true;
        }
    }
}

internal class TransferStats
{
    private long _downloaded;
    private long _uploaded;
    public long Downloaded => Interlocked.Read(ref _downloaded);
    public long Uploaded => Interlocked.Read(ref _uploaded);

    public void AddDownloaded(long bytes)
    {
        Interlocked.Add(ref _downloaded, bytes);
    }

    public void AddUploaded(long bytes)
    {
        Interlocked.Add(ref _uploaded, bytes);
    }
}

internal class FileTransfer : IFileTransfer, IAsyncDisposable, IUnfinishedBytesProvider
{
    // Use centralized constant for block size
    private const int BlockSize = ProtocolConstants.BlockSize;

    private const int HardTimeoutRttMultiplier = 10;
    private const int MaxActivePieces = 32;
    private const int MaxBackgroundTaskRestarts = 3;
    private const int MaxHardTimeoutMs = 30000;

    // Semaphore to limit concurrent overflow piece processing (when queue is full)
    // Prevents unbounded Task.Run spawning that could exhaust thread pool
    private const int MaxOverflowConcurrency = 16;

    private const int MaxRequestAttempts = 3;
    private const int MaxSoftTimeoutMs = 15000;

    // ADAPTIVE TIMEOUTS: Based on peer RTT to handle high-latency connections
    // Hard timeout: Used to give up on a request and retry
    private const int MinHardTimeoutMs = 5000;

    // Soft timeout: Used to trigger duplicate requests to faster peers
    private const int MinSoftTimeoutMs = 3000;

    private const int SoftTimeoutRttMultiplier = 6;
    private static readonly ILogger<FileTransfer> _logger = TorrentLoggerFactory.CreateLogger<FileTransfer>();

    // Track background tasks for proper disposal
    private readonly List<Task> _backgroundTasks = new(3);

    private readonly CancellationTokenSource _cts;
    private readonly Channel<(PeerCommunication Peer, Block Block)> _incomingBlocks;

    // Limit restart attempts to prevent infinite loops
    private readonly SemaphoreSlim _overflowProcessingSemaphore = new(MaxOverflowConcurrency);

    // Track overflow tasks for clean shutdown
    private readonly ConcurrentDictionary<Task, byte> _overflowTasks = new();
    private readonly int _maxPieceQueueCapacity;

    private readonly Channel<PeerCommunication> _peerEvaluationQueue;
    private readonly PiecePicker _piecePicker;
    private readonly BlockRequestTracker _requestTracker = new();
    private readonly RequestScheduler _requestScheduler;
    private readonly RequestTimeoutManager _requestTimeoutManager;
    private readonly PieceCompletionHandler _pieceCompletionHandler;
    private readonly BlockProcessor _blockProcessor;
    private readonly TransferProgressReporter _progressReporter;
    private readonly PieceVerificationWriter _pieceVerificationWriter;
    private readonly SemaphoreSlim _hashSemaphore;
    private readonly SemaphoreSlim _writeSemaphore;
    private readonly PieceStateManager _pieceStateManager;
    private readonly PeerEvaluationScheduler _peerEvaluationScheduler;

    // Bounded queue for piece hashing/writing to prevent thread pool starvation
    private readonly Channel<PieceState> _pieceProcessingQueue;

    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    // BEP 52: dedup outstanding piece-layer hash requests so we don't ask multiple peers (or the
    // same peer repeatedly) for the same chunk while one is in flight. Key: "<piecesRootHex>|<chunkStart>".
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outstandingHashRequests = new();
    private static readonly TimeSpan HashRequestRetryInterval = TimeSpan.FromSeconds(3);
    private int _backgroundTasksFailed;
    private AtomicDisposal _disposal = new();
    // Increased from 32 to 128 for higher parallelism

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset _lastQueueStatusLog = DateTimeOffset.MinValue;

    // Track if background tasks have failed
    public FileTransfer(Torrent torrent, TimeProvider timeProvider)
    {
        _torrent = torrent;
        _timeProvider = timeProvider;
        _piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), _timeProvider, Random.Shared);
        var pieceStateLogger = TorrentLoggerFactory.CreateLogger<PieceStateManager>();
        _pieceStateManager = new PieceStateManager(_piecePicker, pieceStateLogger, MaxActivePieces);

        // Use bounded channels to prevent memory exhaustion under load
        _incomingBlocks = Channel.CreateBounded<(PeerCommunication, Block)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        _peerEvaluationQueue = Channel.CreateBounded<PeerCommunication>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropNewest, // OK to drop evaluation requests - they're periodic
            SingleReader = true
        });

        // THROUGHPUT OPTIMIZATION: Increased from 8 to configurable value (default 64)
        // Higher parallelism prevents backpressure on fast connections (gigabit+)
        // Configurable via torrent.Settings.Transfer.MaxConcurrentPieceProcessing
        int maxConcurrentPieces = Math.Clamp(torrent.Settings.Transfer.MaxConcurrentPieceProcessing, 4, 256);
        _maxPieceQueueCapacity = maxConcurrentPieces;
        _pieceProcessingQueue = Channel.CreateBounded<PieceState>(new BoundedChannelOptions(maxConcurrentPieces)
        {
            FullMode = BoundedChannelFullMode.Wait, // Backpressure: stop accepting blocks if processing lags
            SingleReader = true
        });

        _logger.LogDebug("Piece processing queue initialized with capacity {MaxConcurrentPieces} (configurable via MaxConcurrentPieceProcessing)", maxConcurrentPieces);

        var requestSchedulerLogger = TorrentLoggerFactory.CreateLogger<RequestScheduler>();
        _requestScheduler = new RequestScheduler(new RequestSchedulerOptions
        {
            Torrent = _torrent,
            RequestTracker = _requestTracker,
            PieceStateManager = _pieceStateManager,
            TimeProvider = _timeProvider,
            Logger = requestSchedulerLogger,
            BlockSize = BlockSize,
            MaxRequestsPerPeer = _torrent.Settings.Transfer.MaxRequestsPerPeer,
            GetSoftTimeoutMs = GetAdaptiveSoftTimeout
        }, _piecePicker);

        var requestTimeoutLogger = TorrentLoggerFactory.CreateLogger<RequestTimeoutManager>();
        _requestTimeoutManager = new RequestTimeoutManager(
            _requestTracker,
            RemoveBlockRequest,
            GetAdaptiveHardTimeout,
            requestTimeoutLogger,
            MaxRequestAttempts);

        var pieceCompletionLogger = TorrentLoggerFactory.CreateLogger<PieceCompletionHandler>();
        _pieceCompletionHandler = new PieceCompletionHandler(
            _requestTracker,
            RemoveBlockRequest,
            _torrent,
            pieceCompletionLogger);

        var blockProcessorLogger = TorrentLoggerFactory.CreateLogger<BlockProcessor>();
        var requestCompletionTracker = new RequestCompletionTracker(
            _requestTracker,
            _timeProvider,
            RemoveBlockRequest);
        _blockProcessor = new BlockProcessor(new BlockProcessorOptions
        {
            PieceStateManager = _pieceStateManager,
            BlockSize = BlockSize,
            EnqueuePeerPiece = EnqueuePieceFromPeerAsync,
            EnqueueWebSeedPiece = EnqueuePieceFromWebSeedAsync,
            Downloader = Downloader,
            RequestCompletionTracker = requestCompletionTracker,
            Torrent = _torrent,
            CancelBlockRequest = CancelBlockRequestAsync,
            Logger = blockProcessorLogger
        });

        var progressReporterLogger = TorrentLoggerFactory.CreateLogger<TransferProgressReporter>();
        _progressReporter = new TransferProgressReporter(_torrent, progressReporterLogger);

        var verificationWriterLogger = TorrentLoggerFactory.CreateLogger<PieceVerificationWriter>();
        _pieceVerificationWriter = new PieceVerificationWriter(
            _torrent,
            _timeProvider,
            verificationWriterLogger,
            BlockSize,
            RequestMerkleHashes);

        int maxHash = Math.Clamp(_torrent.Settings.Transfer.MaxConcurrentPieceHashing, 1, 256);
        int maxWrite = Math.Clamp(_torrent.Settings.Transfer.MaxConcurrentPieceWrites, 1, 128);
        _hashSemaphore = new SemaphoreSlim(maxHash, maxHash);
        _writeSemaphore = new SemaphoreSlim(maxWrite, maxWrite);

        var peerSchedulerLogger = TorrentLoggerFactory.CreateLogger<PeerEvaluationScheduler>();
        _peerEvaluationScheduler = new PeerEvaluationScheduler(
            _peerEvaluationQueue,
            EvaluateNextRequestsInternalAsync,
            peerSchedulerLogger);

        _cts = new CancellationTokenSource();

        // Track background tasks for proper disposal and error handling
        _backgroundTasks.Add(RunBackgroundTaskAsync(ProcessIncomingBlocksAsync, "ProcessIncomingBlocks"));
        _backgroundTasks.Add(RunBackgroundTaskAsync(ProcessPeerEvaluationsAsync, "ProcessPeerEvaluations"));
        _backgroundTasks.Add(RunBackgroundTaskAsync(ProcessPieceQueueAsync, "ProcessPieceQueue"));
    }

    long IFileTransfer.Downloaded => Downloader.Downloaded;
    public TransferStats Downloader { get; } = new();
    public bool EndGameMode => _torrent.Pieces.Count - _torrent.Pieces.ReceivedCount < 10 && _torrent.Pieces.Count > 10;

    /// <summary>
    /// Returns true if any background processing tasks have failed.
    /// </summary>
    public bool HasBackgroundTaskFailure => Interlocked.CompareExchange(ref _backgroundTasksFailed, 0, 0) > 0;

    public bool IsDisposed => _disposal.IsDisposed;

    // IFileTransfer interface implementation
    long IFileTransfer.Uploaded => Uploader.Uploaded;

    public TransferStats Uploader { get; } = new();

    /// <summary>
    /// BEP-3: Handle Cancel message from peer - they no longer want a previously requested block.
    /// Since we fulfill requests immediately (no queue), this is primarily for logging.
    /// </summary>
    public static void BlockRequestCancelled(PeerCommunication peer, PeerMessage msg)
    {
        // In this implementation, requests are fulfilled immediately without queuing,
        // so Cancel is mostly informational. A more sophisticated implementation might
        // maintain an upload queue where pending requests could be removed.
        _logger.LogDebug("Request cancelled by {RemoteEndPoint}: {PieceIndex}:{BlockOffset}", peer.RemoteEndPoint, msg.PieceIndex, msg.BlockOffset);
    }

    public async Task BlockReceivedAsync(PeerCommunication peer, Block block)
    {
        try
        {
            await _incomingBlocks.Writer.WriteAsync((peer, block), _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Torrent stopping
            block.Dispose();
        }
        catch (Exception ex)
        {
            block.Dispose();
            _logger.LogError(ex, "Failed to enqueue block from {RemoteEndPoint}", peer.RemoteEndPoint);
        }
    }

    public async Task BlockRejectedAsync(PeerCommunication peer, PeerMessage msg)
    {
        _logger.LogDebug("Request rejected by {RemoteEndPoint}: {PieceIndex}:{BlockOffset}", peer.RemoteEndPoint, msg.PieceIndex, msg.BlockOffset);

        if (_requestTracker.TryGetPeerRequests(peer, out var requests))
        {
            var key = (msg.PieceIndex, msg.BlockOffset);
            if (requests.TryRemove(key, out var r))
            {
                RemoveBlockRequest(r.PieceIndex, r.Offset, peer);
            }
        }

        // Immediately try to re-request rejected blocks from other peers
        // This is important when a peer chokes us and rejects all pending requests
        foreach (var otherPeer in _torrent.PeersInternal.GetConnectedPeersInternal())
        {
            if (otherPeer != peer && !otherPeer.PeerChoking && otherPeer.PeerPieces.HasPiece(msg.PieceIndex))
            {
                await EvaluateNextRequestsAsync(otherPeer, immediate: false).ConfigureAwait(false);
                break; // Only need to trigger one peer, they'll pick up the block
            }
        }
    }

    public async Task BlockRequestedAsync(PeerCommunication peer, PeerMessage msg)
    {
        // Check conditions for rejecting a request
        bool reject = false;
        if (peer.AmChoking && !peer.IsAllowedFast(msg.PieceIndex))
        {
            reject = true;
        }

        if (!_torrent.Pieces.HasPiece(msg.PieceIndex))
        {
            reject = true;
        }

        // BEP 16: In superseed mode, only allow requests for assigned pieces
        if (!reject && !_torrent.SuperSeedManager.ShouldAllowRequest(peer, msg.PieceIndex))
        {
            reject = true;
        }

        if (msg.BlockLength <= 0 || msg.BlockLength > BlockSize)
        {
            reject = true;
        }

        if (msg.BlockOffset < 0)
        {
            reject = true;
        }

        if (!IsValidUploadRequest(msg))
        {
            reject = true;
        }

        if (reject)
        {
            if (peer.RemoteSupportsExtensions)
            {
                await peer.SendRejectAsync(new BlockRequest
                {
                    PieceIndex = msg.PieceIndex,
                    Offset = msg.BlockOffset,
                    Length = msg.BlockLength
                }).ConfigureAwait(false);
            }
            return;
        }

        Block? block = null;
        try
        {
            long pieceOffset = msg.PieceIndex * _torrent.InfoFile.Info.PieceSize;
            long globalOffset = pieceOffset + msg.BlockOffset;

            block = new Block(msg.PieceIndex, msg.BlockOffset, msg.BlockLength);
            await _torrent.FilesInternal.ReadAsync(globalOffset, block.Buffer.AsMemory(0, msg.BlockLength), _cts.Token).ConfigureAwait(false);

            var response = new PeerMessage(MessageId.Piece)
            {
                PieceIndex = msg.PieceIndex,
                BlockOffset = msg.BlockOffset,
                PooledBlock = block
            };

            await peer.SendMessageAsync(response).ConfigureAwait(false);
            block = null;

            Uploader.AddUploaded(msg.BlockLength);
            peer.AddUploaded(msg.BlockLength);
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fulfil request from {RemoteEndPoint}", peer.RemoteEndPoint);
            block?.Dispose();
        }
    }

    private bool IsValidUploadRequest(PeerMessage msg)
    {
        if (msg.PieceIndex < 0 || msg.PieceIndex >= _torrent.Pieces.Count)
        {
            return false;
        }

        return IsValidUploadRequestRange(msg.BlockOffset, msg.BlockLength, _torrent.InfoFile.Info.GetPieceSize(msg.PieceIndex));
    }

    internal static bool IsValidUploadRequestRange(int offset, int length, long pieceSize)
    {
        if (offset < 0 || length <= 0 || pieceSize < 0)
        {
            return false;
        }

        return (long)offset + length <= pieceSize;
    }

    public void DecrementAvailability(int pieceIndex)
    {
        _piecePicker.DecrementAvailability(pieceIndex);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public async Task EvaluateNextRequestsAsync(PeerCommunication peer)
    {
        await EvaluateNextRequestsAsync(peer, immediate: false).ConfigureAwait(false);
    }

    /// <summary>
    /// SPEED STABILITY FIX: Support immediate evaluation for freshly unchoked peers.
    /// When a peer unchokes us, we should send requests immediately rather than
    /// waiting for the async queue, which can cause delays of several seconds.
    /// </summary>
    public async Task EvaluateNextRequestsAsync(PeerCommunication peer, bool immediate)
    {
        if (immediate)
        {
            // Bypass the queue and evaluate directly for time-critical events (unchoke)
            try
            {
                await EvaluateNextRequestsInternalAsync(peer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in immediate peer evaluation for {RemoteEndPoint}", peer.RemoteEndPoint);
            }
            return;
        }

        // Normal path: queue for async processing
        _peerEvaluationScheduler.Enqueue(peer);
    }

    public long GetUnfinishedBytes()
    {
        long bytes = 0;
        foreach (var p in _pieceStateManager.ActivePieces.Values)
        {
            bytes += (long)p.ReceivedCount * BlockSize;
        }
        return bytes;
    }

    public long GetUnfinishedBytesForFile(int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= _torrent.InfoFile.Info.Files.Count)
        {
            return 0;
        }

        var file = _torrent.InfoFile.Info.Files[fileIndex];
        var (firstPiece, lastPiece) = _torrent.InfoFile.Info.GetPieceRangeForFile(fileIndex);
        if (firstPiece == -1)
        {
            return 0;
        }

        long bytes = 0;
        uint pieceSize = _torrent.InfoFile.Info.PieceSize;
        long fullSize = _torrent.InfoFile.Info.FullSize;

        for (int i = firstPiece; i <= lastPiece; i++)
        {
            if (_pieceStateManager.TryGetPiece(i, out var p))
            {
                bytes += p.GetReceivedBytes(i * pieceSize, pieceSize, fullSize, file.Offset, file.Size);
            }
        }
        return bytes;
    }

    public List<TorrentStateData.UnfinishedPieceData> GetUnfinishedPiecesState()
    {
        // Cap the number of saved pieces to limit memory usage during serialization.
        // Prioritize pieces with the most progress (highest received block count).
        const int MaxSavedPieces = 32;

        var snapshot = _pieceStateManager.ActivePieces.Values.ToList();

        // Sort by progress descending so we save the most complete pieces first
        snapshot.Sort((a, b) => b.ReceivedCount.CompareTo(a.ReceivedCount));

        var list = new List<TorrentStateData.UnfinishedPieceData>();
        foreach (var piece in snapshot)
        {
            if (list.Count >= MaxSavedPieces)
            {
                break;
            }

            if (piece.ReceivedCount > 0)
            {
                long pSize = _torrent.InfoFile.Info.GetPieceSize(piece.Index);

                var data = new byte[pSize];
                for (int i = 0; i < piece.BlockData.Length; i++)
                {
                    var b = piece.BlockData[i];
                    if (b != null)
                    {
                        Array.Copy(b.Buffer, 0, data, i * BlockSize, b.Length);
                    }
                }

                list.Add(new TorrentStateData.UnfinishedPieceData
                {
                    Index = piece.Index,
                    Blocks = (bool[])piece.Blocks.Clone(),
                    Data = data
                });
            }
        }

        return list;
    }

    public long GetUnfinishedSelectedBytes(IReadOnlyList<FileSelection>? selection)
    {
        if (selection == null || selection.Count == 0)
        {
            return GetUnfinishedBytes();
        }

        long bytes = 0;
        foreach (var p in _pieceStateManager.ActivePieces.Values)
        {
            if (_torrent.InfoFile.Info.IsPieceNeeded(p.Index, selection))
            {
                bytes += (long)p.ReceivedCount * BlockSize;
            }
        }
        return bytes;
    }

    public void IncrementAvailability(int pieceIndex)
    {
        _piecePicker.IncrementAvailability(pieceIndex);
    }

    public void InvalidateSelection()
    {
        _piecePicker.InvalidateSelection();
    }

    /// <summary>
    /// BEP 19: Checks if a piece is currently being downloaded.
    /// Used by WebSeedManager to avoid duplicate downloads.
    /// </summary>
    public bool IsPieceActive(int pieceIndex)
    {
        return _pieceStateManager.ContainsPiece(pieceIndex);
    }

    public void LoadUnfinishedPiecesState(List<TorrentStateData.UnfinishedPieceData> pieces)
    {
        foreach (var p in pieces)
        {
            if (_torrent.Pieces.HasPiece(p.Index))
            {
                continue;
            }

            var state = new PieceState(p.Index, p.Blocks.Length);
            Array.Copy(p.Blocks, state.Blocks, p.Blocks.Length);

            int count = 0;
            for (int i = 0; i < state.Blocks.Length; i++)
            {
                if (state.Blocks[i])
                {
                    count++;
                    int offset = i * BlockSize;
                    int len = Math.Min(BlockSize, p.Data.Length - offset);
                    var block = new Block(p.Index, offset, len);
                    Array.Copy(p.Data, offset, block.Buffer, 0, len);
                    state.BlockData[i] = block;
                }
            }

            // Set received count for initialization (before concurrent access)
            state.SetReceivedCountForInit(count);

            if (state.ReceivedCount > 0)
            {
                _pieceStateManager.AddOrReplacePiece(state);
            }
        }
    }

    public void PiecesAvailabilityChanged()
    {
        // Handled by PiecePicker internally implicitly via increment/decrement
        // but if we need to re-sort, we can trigger invalidation
        _piecePicker.InvalidateSelection();
    }

    public void RefreshSelection()
    {
        _piecePicker.RefreshSelection();
    }

    public void RegisterPeerAvailability(PeerCommunication peer)
    {
        _piecePicker.RegisterPeerAvailability(peer);
    }

    public async Task RequestBlocksAsync(PeerCommunication peer)
    {
        await EvaluateNextRequestsAsync(peer, immediate: false).ConfigureAwait(false);
    }

    /// <summary>
    /// SPEED STABILITY FIX: Request blocks with optional immediate evaluation.
    /// Use immediate=true for time-critical events like Unchoke to avoid queue delays.
    /// </summary>
    public async Task RequestBlocksAsync(PeerCommunication peer, bool immediate)
    {
        await EvaluateNextRequestsAsync(peer, immediate).ConfigureAwait(false);
    }

    public void UnregisterPeerAvailability(PeerCommunication peer)
    {
        _piecePicker.UnregisterPeerAvailability(peer);
        _requestTracker.RemovePeer(peer);
    }

    public void Update()
    {
        if (!_torrent.Started)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        if ((now - _lastQueueStatusLog).TotalSeconds >= 5)
        {
            _lastQueueStatusLog = now;
            int totalPendingRequests = 0;
            int peersWithRequests = 0;
            int unchokedPeers = 0;
            int peerCount = 0;
            int oldestRequestAgeMs = 0;

            foreach (var kv in _requestTracker.EnumeratePeerRequests().ToArray())
            {
                if (!kv.Value.IsEmpty)
                {
                    totalPendingRequests += kv.Value.Count;
                    peersWithRequests++;
                    foreach (var req in kv.Value.Values)
                    {
                        int ageMs = (int)(now - req.Timestamp).TotalMilliseconds;
                        if (ageMs > oldestRequestAgeMs)
                        {
                            oldestRequestAgeMs = ageMs;
                        }
                    }
                }
            }

            // Count peers and unchoked inline to avoid ToList() allocation
            foreach (var peer in _torrent.PeersInternal.GetConnectedPeersInternal())
            {
                peerCount++;
                if (!peer.PeerChoking)
                {
                    unchokedPeers++;
                }
            }

            bool isStarved = !_torrent.Finished && totalPendingRequests == 0 && peerCount > 0;
            bool isStalled = oldestRequestAgeMs > 10000;

            if (isStarved)
            {
                _logger.LogTrace("REQUEST STARVATION: No pending requests! peers={PeerCount}, unchoked={Unchoked}, activePieces={ActivePieces}, finished={Finished}", peerCount, unchokedPeers, _pieceStateManager.Count, _torrent.Finished);
            }
            else if (isStalled)
            {
                _logger.LogTrace("REQUEST STALL: Oldest request is {Age}ms old! pendingRequests={Pending}, peersWithRequests={PeersWithRequests}", oldestRequestAgeMs, totalPendingRequests, peersWithRequests);
            }

            _logger.LogTrace("Transfer status: peers={PeerCount}, unchoked={Unchoked}, peersWithRequests={PeersWithRequests}, pendingRequests={Pending}, activePieces={ActivePieces}/{MaxActive}, blockIndex={BlockIndex}, oldestReq={OldestReq}ms, endGame={EndGame}",
                peerCount, unchokedPeers, peersWithRequests, totalPendingRequests, _pieceStateManager.Count, MaxActivePieces, _requestTracker.BlockRequestIndexCount, oldestRequestAgeMs, EndGameMode);
        }

        _requestTimeoutManager.ProcessTimeouts(now, EndGameMode);

        // Get peers list for sorting and requesting (ToList needed here for Sort)
        var peers = new List<PeerCommunication>();
        peers.AddRange(_torrent.PeersInternal.GetConnectedPeersInternal());

        // Sort in-place: non-choking peers first (by speed desc), then choking peers
        peers.Sort((a, b) =>
        {
            // Non-choking peers come first
            int chokingCompare = a.PeerChoking.CompareTo(b.PeerChoking);
            if (chokingCompare != 0)
            {
                return chokingCompare;
            }
            // Within same choking status, sort by speed descending
            return b.SmoothedDownloadSpeed.CompareTo(a.SmoothedDownloadSpeed);
        });

        foreach (var peer in peers)
        {
            try { _ = RequestBlocksAsync(peer); }
            catch (Exception ex) { _logger.LogError(ex, "RequestBlocks error for {RemoteEndPoint}", peer.RemoteEndPoint); }
        }

        if ((now - _lastPrune).TotalSeconds > 10)
        {
            _lastPrune = now;
            PruneStalePieces();
        }
    }

    /// <summary>
    /// BEP 19: Receives a block downloaded from a web seed.
    /// Creates the piece state if needed and processes the block.
    /// </summary>
    public async Task WebSeedBlockReceivedAsync(Block block)
    {
        await _blockProcessor.HandleWebSeedBlockReceivedAsync(block, _cts.Token).ConfigureAwait(false);
    }

    internal Task ProcessBlockAsync(PeerCommunication peer, Block block)
    {
        return _blockProcessor.HandlePeerBlockAsync(peer, block);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_disposal.MarkDisposed())
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                _incomingBlocks.Writer.TryComplete();
                _peerEvaluationQueue.Writer.TryComplete();
                _pieceProcessingQueue.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during FileTransfer.Stop()");
            }

            try
            {
                if (_backgroundTasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(_backgroundTasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogTrace(ex, "Background tasks did not complete within timeout during disposal");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error waiting for background tasks");
            }

            try
            {
                var overflowTasksSnapshot = _overflowTasks.Keys.ToArray();
                if (overflowTasksSnapshot.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(overflowTasksSnapshot).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogTrace(ex, "Overflow tasks did not complete within timeout during disposal");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error waiting for overflow tasks");
            }

            _pieceStateManager.Dispose();
            _cts.Dispose();
            _overflowProcessingSemaphore.Dispose();
            _hashSemaphore.Dispose();
            _writeSemaphore.Dispose();
        }
    }

    /// <summary>
    /// Calculates adaptive hard timeout based on peer's smoothed RTT.
    /// Returns timeout in milliseconds, clamped to [MinHardTimeoutMs, MaxHardTimeoutMs].
    /// </summary>
    private static int GetAdaptiveHardTimeout(PeerCommunication peer)
    {
        int rtt = peer.SmoothedRttMs;
        if (rtt <= 0)
        {
            return MinHardTimeoutMs; // No RTT data yet, use minimum
        }

        int adaptiveTimeout = rtt * HardTimeoutRttMultiplier;
        return Math.Clamp(adaptiveTimeout, MinHardTimeoutMs, MaxHardTimeoutMs);
    }

    /// <summary>
    /// Calculates adaptive soft timeout based on peer's smoothed RTT.
    /// Returns timeout in milliseconds, clamped to [MinSoftTimeoutMs, MaxSoftTimeoutMs].
    /// </summary>
    private static int GetAdaptiveSoftTimeout(PeerCommunication peer)
    {
        int rtt = peer.SmoothedRttMs;
        if (rtt <= 0)
        {
            return MinSoftTimeoutMs; // No RTT data yet, use minimum
        }

        int adaptiveTimeout = rtt * SoftTimeoutRttMultiplier;
        return Math.Clamp(adaptiveTimeout, MinSoftTimeoutMs, MaxSoftTimeoutMs);
    }

    private async Task CancelBlockRequestAsync(int pieceIndex, int offset, PeerCommunication source)
    {
        var key = (pieceIndex, offset);
        if (_requestTracker.TryGetBlockPeers(key, out var list))
        {
            // Snapshot peers to cancel (avoids modification during iteration)
            var peersToCancel = new List<(PeerCommunication Peer, BlockRequest Request)>();
            foreach (var kv in list.ToArray())
            {
                if (kv.Key != source)
                {
                    peersToCancel.Add((kv.Key, kv.Value));
                }
            }

            // Now cancel and remove
            foreach (var (peer, req) in peersToCancel)
            {
                await peer.SendMessageAsync(new PeerMessage(MessageId.Cancel)
                {
                    PieceIndex = pieceIndex,
                    BlockOffset = offset,
                    BlockLength = req.Length
                }).ConfigureAwait(false);

                _requestTracker.TryRemovePeerRequest(peer, key, out _);

                RemoveBlockRequest(pieceIndex, offset, peer);
            }
        }
    }

    private bool IsPieceQueueFull()
    {
        return _pieceProcessingQueue.Reader.CanCount
            && _pieceProcessingQueue.Reader.Count >= _maxPieceQueueCapacity;
    }

    private Task EvaluateNextRequestsInternalAsync(PeerCommunication peer)
    {
        return _requestScheduler.EvaluateNextRequestsAsync(peer, EndGameMode, IsPieceQueueFull);
    }

    private async Task ProcessIncomingBlocksAsync(CancellationToken ct)
    {
        while (await _incomingBlocks.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_incomingBlocks.Reader.TryRead(out var item))
            {
                try
                {
                    await _blockProcessor.HandlePeerBlockAsync(item.Peer, item.Block).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing block from {RemoteEndPoint}", item.Peer.RemoteEndPoint);
                    // Ensure block is disposed even on error
                    item.Block.Dispose();
                }
            }
        }
    }

    private Task EnqueuePieceFromPeerAsync(PieceState pieceToProcess)
    {
        if (_pieceProcessingQueue.Writer.TryWrite(pieceToProcess))
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning("Piece processing queue full - forcing immediate processing for piece {PieceIndex}. Disk I/O may be bottlenecked", pieceToProcess.Index);
        var task = ProcessPieceWithOverflowLimitAsync(pieceToProcess, _cts.Token);
        _overflowTasks.TryAdd(task, 0);

        _ = task.ContinueWith(t =>
        {
            _overflowTasks.TryRemove(t, out _);
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Overflow piece processing failed");
            }
        }, TaskScheduler.Default);

        return Task.CompletedTask;
    }

    private async Task EnqueuePieceFromWebSeedAsync(PieceState pieceToProcess, CancellationToken ct)
    {
        if (_pieceProcessingQueue.Writer.TryWrite(pieceToProcess))
        {
            return;
        }

        _logger.LogWarning("Piece processing queue full - piece {PieceIndex} waiting for queue space", pieceToProcess.Index);
        await _pieceProcessingQueue.Writer.WriteAsync(pieceToProcess, ct).ConfigureAwait(false);
    }

    private async Task ProcessPeerEvaluationsAsync(CancellationToken ct)
    {
        await _peerEvaluationScheduler.RunAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessPieceQueueAsync(CancellationToken ct)
    {
        while (await _pieceProcessingQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_pieceProcessingQueue.Reader.TryRead(out var piece))
            {
                await ProcessSinglePieceAsync(piece, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Processes a piece with bounded concurrency when the main queue is full.
    /// Uses semaphore to prevent unbounded task spawning.
    /// </summary>
    private async Task ProcessPieceWithOverflowLimitAsync(PieceState pieceToProcess, CancellationToken ct = default)
    {
        // Wait for a slot (with timeout to prevent indefinite blocking)
        if (!await _overflowProcessingSemaphore.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
        {
            _logger.LogError("Overflow processing timeout for piece {PieceIndex} - system severely overloaded", pieceToProcess.Index);
            // Reset piece state so it can be re-requested
            pieceToProcess.Reset();
            return;
        }

        try
        {
            await ProcessSinglePieceAsync(pieceToProcess, ct).ConfigureAwait(false);
        }
        finally
        {
            _overflowProcessingSemaphore.Release();
        }
    }

    private async Task ProcessSinglePieceAsync(PieceState pieceToProcess, CancellationToken ct = default)
    {
        try
        {
            PieceVerificationOutcome outcome;
            await _hashSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                outcome = await _pieceVerificationWriter.VerifyAsync(pieceToProcess, ct).ConfigureAwait(false);
            }
            finally
            {
                _hashSemaphore.Release();
            }

            using (outcome)
            {
                bool writeFailed = false;
                if (outcome.HashSuccess)
                {
                    await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        bool writeSuccess = await _pieceVerificationWriter.WriteAsync(pieceToProcess, outcome.PieceSize, outcome.FullData, ct).ConfigureAwait(false);
                        writeFailed = !writeSuccess;
                    }
                    finally
                    {
                        _writeSemaphore.Release();
                    }
                }

                if (outcome.HashFailed || outcome.HashSuccess || writeFailed)
                {
                    if (writeFailed)
                    {
                        // Disk error - reset state so we can retry, but don't penalize peers
                        pieceToProcess.Reset();
                        _logger.LogWarning("Piece {PieceIndex} write failed, state reset for retry", pieceToProcess.Index);
                    }
                    else if (outcome.HashFailed)
                    {
                        pieceToProcess.Reset();
                        _logger.LogWarning("Piece {PieceIndex} failed hash, will be retried", pieceToProcess.Index);

                        foreach (var p in pieceToProcess.Contributors)
                        {
                            p.Strikes++;
                            if (p.Strikes >= 3)
                            {
                                _logger.LogWarning("Banning peer {RemoteEndPoint} due to hash failures", p.RemoteEndPoint);
                                await p.CloseAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    else if (outcome.HashSuccess)
                    {
                        // Use atomic dispose method to prevent races
                        pieceToProcess.CompleteAndDispose();

                        _pieceStateManager.TryRemovePiece(pieceToProcess.Index, out _);

                        _torrent.Pieces.AddPiece(pieceToProcess.Index);
                        // Notify Torrent of verification for cached stats update
                        _torrent.OnPieceVerified(pieceToProcess.Index);

                        _progressReporter.ReportPieceCompleted(pieceToProcess.Index);

                        await _pieceCompletionHandler.HandlePieceCompletedAsync(pieceToProcess.Index, EndGameMode).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            // Log unhandled exceptions in fire-and-forget task to prevent silent failures
            _logger.LogError(ex, "Error processing piece {PieceIndex}", pieceToProcess.Index);
        }
    }

    private void PruneStalePieces()
    {
        _pieceStateManager.PruneStalePieces();
    }

    private void RemoveBlockRequest(int piece, int offset, PeerCommunication peer)
    {
        _requestTracker.RemoveBlockRequest(piece, offset, peer);
    }

    /// <summary>
    /// BEP 30: Request Merkle hashes for a piece from connected peers.
    /// </summary>
    private void RequestMerkleHashes(int pieceIndex)
    {
        if (_torrent.InfoFile.Info.IsV2)
        {
            var request = _torrent.InfoFile.Info.GetV2HashRequestForPiece(pieceIndex);
            if (request == null)
            {
                return;
            }

            string requestKey = $"{Convert.ToHexString(request.PiecesRoot)}|{request.Index}";
            var now = _timeProvider.GetUtcNow();
            if (_outstandingHashRequests.TryGetValue(requestKey, out var lastRequested)
                && now - lastRequested < HashRequestRetryInterval)
            {
                return;
            }

            foreach (var peer in _torrent.PeersInternal.GetConnectedPeersInternal())
            {
                if (peer.RemoteSupportsV2 && peer.PeerPieces.HasPiece(pieceIndex))
                {
                    _outstandingHashRequests[requestKey] = now;
                    _ = peer.SendHashRequestAsync(request.PiecesRoot, request.BaseLayer, request.Index, request.Length, request.ProofLayers)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                _outstandingHashRequests.TryRemove(requestKey, out _);
                                _logger.LogDebug(t.Exception, "BEP 52: Failed to request piece layer for piece {PieceIndex}", pieceIndex);
                            }
                        }, TaskScheduler.Default);
                    _logger.LogDebug("BEP 52: Requested piece layer for file root {PiecesRoot} from {RemoteEndPoint}", Convert.ToHexString(request.PiecesRoot), peer.RemoteEndPoint);
                    return;
                }
            }

            _logger.LogDebug("BEP 52: No peers available to request hashes for piece {PieceIndex}", pieceIndex);
            return;
        }

        foreach (var peer in _torrent.PeersInternal.GetConnectedPeersInternal())
        {
            // Only request from peers that support ut_hash_piece and have this piece
            if (peer.UtHashPiece?.RemoteMessageId.HasValue == true && peer.PeerPieces.HasPiece(pieceIndex))
            {
                peer.UtHashPiece.RequestHashes(pieceIndex);
                _logger.LogDebug("BEP 30: Requested hashes for piece {PieceIndex} from {RemoteEndPoint}", pieceIndex, peer.RemoteEndPoint);
                return; // Only request from one peer at a time
            }
        }

        _logger.LogDebug("BEP 30: No peers available to request hashes for piece {PieceIndex}", pieceIndex);
    }

    private async Task RunBackgroundTaskAsync(Func<CancellationToken, Task> taskFunc, string taskName)
    {
        int restartCount = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await taskFunc(_cts.Token).ConfigureAwait(false);
                break; // Normal completion
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown - expected
                break;
            }
            catch (Exception ex)
            {
                restartCount++;
                if (restartCount > MaxBackgroundTaskRestarts)
                {
                    Interlocked.Increment(ref _backgroundTasksFailed);
                    _logger.LogError(ex, "CRITICAL: Background task '{TaskName}' failed {RestartCount} times, giving up", taskName, restartCount);
                    // Alert the system about the failure
                    _torrent.FireErrorEvent(new TorrentException($"Background task '{taskName}' failed after {restartCount} attempts.", _torrent.Hash, ex));
                    _torrent.Alerts.TorrentAlert(AlertId.TorrentInterrupted, _torrent);
                    break;
                }

                _logger.LogWarning(ex, "Background task '{TaskName}' failed (attempt {RestartCount}/{MaxRestarts}), restarting in 1s", taskName, restartCount, MaxBackgroundTaskRestarts);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), _timeProvider, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
