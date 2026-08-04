using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.PiecePicking;
using System.Collections.Concurrent;
using System.Threading.Channels;
using PeerSharp.Messages;
using PeerSharp.Internals.Transfers;

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
    public HashSet<PeerCommunication> Contributors { get; } = [];
    public int Index { get; }

    private PeerCommunication? _retryOwner;
    private DateTimeOffset _retryClaimedAt;

    /// <summary>
    /// How many times this piece has been assembled and failed its hash. Deliberately survives
    /// <see cref="Reset"/>, which clears everything else: it is the count of attempts, not of the
    /// current attempt.
    /// </summary>
    public int HashFailures { get; private set; }

    public void RecordHashFailure()
    {
        lock (_lock)
        {
            HashFailures++;
        }
    }

    /// <summary>
    /// Whether this peer may be asked for blocks of this piece now.
    ///
    /// <para>
    /// Always, until the piece has failed its hash. After that it is asked of one peer at a time, so
    /// that the next failure names its author instead of implicating everyone who happened to
    /// contribute. A piece assembled from six peers and failing tells you almost nothing; the same
    /// piece taken whole from one peer and failing tells you everything.
    /// </para>
    ///
    /// <para>
    /// The claim expires. Restricting who may be asked cannot be allowed to wedge the piece on a peer
    /// that has stopped answering - the point is to find the bad peer, not to wait forever for a quiet
    /// one - so once the claim goes stale the next peer to ask takes it over.
    /// </para>
    /// </summary>
    public bool TryClaimForRetry(PeerCommunication peer, DateTimeOffset now, TimeSpan claimTimeout)
    {
        lock (_lock)
        {
            if (HashFailures == 0)
            {
                return true;
            }

            if (_retryOwner is null || now - _retryClaimedAt > claimTimeout)
            {
                _retryOwner = peer;
                _retryClaimedAt = now;
                return true;
            }

            return ReferenceEquals(_retryOwner, peer);
        }
    }

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

            // A fresh attempt gets a fresh candidate. HashFailures is deliberately not cleared - it is
            // what decides that this piece is being retried at all.
            _retryOwner = null;

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

            if (blockIdx < 0 || blockIdx >= Blocks.Length)
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

            if (blockIdx < 0 || blockIdx >= Blocks.Length)
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
    /// Atomically checks whether the piece is complete and claims write responsibility for it.
    /// Returns true only when the piece is fully received and this call was the one that claimed
    /// it, so exactly one caller ever writes a given piece.
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
    private readonly ILogger<FileTransfer> Logger;

    // Track background tasks for proper disposal
    private readonly List<Task> _backgroundTasks = new(3);

    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// The stopping token, taken once from <see cref="_cts"/> and held.
    ///
    /// <para>
    /// <c>CancellationTokenSource.Token</c> throws once the source is disposed, and work driven by peers
    /// rather than by our own background loops is still arriving at that point - a block already on the
    /// wire does not know the torrent is stopping. Disposal waits only for the loops it owns, so reading
    /// the property from those paths raced with it. A token captured beforehand keeps working: it simply
    /// reports cancellation, and because cancellation happens before disposal, callers that pass it
    /// short-circuit rather than trying to register on a dead source.
    /// </para>
    /// </summary>
    private readonly CancellationToken _stoppingToken;
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
    private readonly UploadQueueManager _uploadQueueManager;

    // Bounded queue for piece hashing/writing to prevent thread pool starvation
    private readonly Channel<PieceState> _pieceProcessingQueue;

    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private static readonly TimeSpan HashRequestRetryInterval = TimeSpan.FromSeconds(3);
    private readonly MerkleHashRequestCoordinator _merkleHashRequestCoordinator = new(HashRequestRetryInterval);
    private int _backgroundTasksFailed;
    private AtomicDisposal _disposal = new();
    // Increased from 32 to 128 for higher parallelism

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset _lastQueueStatusLog = DateTimeOffset.MinValue;

    // Track if background tasks have failed
    public FileTransfer(Torrent torrent, TimeProvider timeProvider)
        : this(torrent, timeProvider, NullLoggerFactory.Instance)
    {
    }

    internal FileTransfer(Torrent torrent, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _torrent = torrent;
        _timeProvider = timeProvider;
        Logger = loggerFactory.CreateLogger<FileTransfer>();
        _piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), _timeProvider, Random.Shared, loggerFactory);
        var pieceStateLogger = loggerFactory.CreateLogger<PieceStateManager>();
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

        Logger.LogDebug("Piece processing queue initialized with capacity {MaxConcurrentPieces} (configurable via MaxConcurrentPieceProcessing)", maxConcurrentPieces);

        var requestSchedulerLogger = loggerFactory.CreateLogger<RequestScheduler>();
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

        var requestTimeoutLogger = loggerFactory.CreateLogger<RequestTimeoutManager>();
        _requestTimeoutManager = new RequestTimeoutManager(
            _requestTracker,
            RemoveBlockRequest,
            GetAdaptiveHardTimeout,
            requestTimeoutLogger,
            MaxRequestAttempts);

        var pieceCompletionLogger = loggerFactory.CreateLogger<PieceCompletionHandler>();
        _pieceCompletionHandler = new PieceCompletionHandler(
            _requestTracker,
            RemoveBlockRequest,
            _torrent,
            pieceCompletionLogger);

        var blockProcessorLogger = loggerFactory.CreateLogger<BlockProcessor>();
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

        var progressReporterLogger = loggerFactory.CreateLogger<TransferProgressReporter>();
        _progressReporter = new TransferProgressReporter(_torrent, progressReporterLogger);

        var verificationWriterLogger = loggerFactory.CreateLogger<PieceVerificationWriter>();
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

        var peerSchedulerLogger = loggerFactory.CreateLogger<PeerEvaluationScheduler>();
        _peerEvaluationScheduler = new PeerEvaluationScheduler(
            _peerEvaluationQueue,
            EvaluateNextRequestsInternalAsync,
            peerSchedulerLogger);

        _cts = new CancellationTokenSource();
        _stoppingToken = _cts.Token;

        var uploadQueueLogger = loggerFactory.CreateLogger<UploadQueueManager>();
        _uploadQueueManager = new UploadQueueManager(ExecuteUploadItemAsync, uploadQueueLogger, _stoppingToken);

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
    /// Marks the item cancelled in the upload queue so the pump skips or rejects it.
    /// </summary>
    public void BlockRequestCancelled(PeerCommunication peer, PeerMessage msg)
    {
        _uploadQueueManager.Cancel(peer, msg.PieceIndex, msg.BlockOffset);
        Logger.LogDebug("Request cancelled by {RemoteEndPoint}: {PieceIndex}:{BlockOffset}", peer.RemoteEndPoint, msg.PieceIndex, msg.BlockOffset);
    }

    public async Task BlockReceivedAsync(PeerCommunication peer, Block block)
    {
        try
        {
            await _incomingBlocks.Writer.WriteAsync((peer, block), _stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
        {
            // The torrent is stopping. Blocks keep arriving for a moment either way, because the peer
            // sent them before it knew, so this is the ordinary end of a transfer rather than a fault.
            block.Dispose();
        }
        catch (Exception ex)
        {
            block.Dispose();
            Logger.LogError(ex, "Failed to enqueue block from {RemoteEndPoint}", peer.RemoteEndPoint);
            // The block was answered but dropped - free its request slot so the scheduler
            // can re-request it instead of waiting for the stale-piece sweep.
            ReleasePendingRequest(peer, block);
        }
    }

    /// <summary>
    /// Removes the in-flight request entry for a block that arrived but could not be
    /// processed, so the request pipeline is not depressed until the stale-piece sweep.
    /// </summary>
    private void ReleasePendingRequest(PeerCommunication peer, Block block)
    {
        var key = (block.PieceIndex, block.Offset);
        if (_requestTracker.TryRemovePeerRequest(peer, key, out var r))
        {
            RemoveBlockRequest(r.PieceIndex, r.Offset, peer);
        }
    }

    /// <summary>
    /// Non-recoverable storage failure (disk full, permanently failed file): records the
    /// error on the torrent, notifies the application, and stops the torrent so it does not
    /// keep re-downloading pieces it can never store. Returns the background stop task.
    /// The stop must not be awaited from inside FileTransfer's own processing loops
    /// (StopAsync waits for them), which is why callers fire-and-forget this.
    /// </summary>
    internal Task HandleFatalStorageErrorAsync(StorageException ex)
    {
        Logger.LogCritical(ex, "Fatal storage error for {TorrentName} - stopping torrent", _torrent.Name);
        _torrent.FireErrorEvent(new TorrentException($"Fatal storage error: {ex.Message}", _torrent.Hash, ex));

        return Task.Run(async () =>
        {
            try
            {
                await _torrent.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx)
            {
                Logger.LogError(stopEx, "Failed to stop torrent after fatal storage error");
            }
        });
    }

    public async Task BlockRejectedAsync(PeerCommunication peer, PeerMessage msg)
    {
        Logger.LogDebug("Request rejected by {RemoteEndPoint}: {PieceIndex}:{BlockOffset}", peer.RemoteEndPoint, msg.PieceIndex, msg.BlockOffset);

        // Check the reject describes a real block before letting it touch our state. A reject naming an
        // arbitrary piece would otherwise let a peer withdraw offers it never made, or clear requests
        // belonging to a different block. libtorrent validates the same three things and ignores the
        // message outright when they do not hold.
        if (!IsValidUploadRequest(msg))
        {
            Logger.LogDebug(
                "Ignoring malformed reject from {RemoteEndPoint}: {PieceIndex}:{BlockOffset} ({Length}B)",
                peer.RemoteEndPoint, msg.PieceIndex, msg.BlockOffset, msg.BlockLength);
            return;
        }

        var key = (msg.PieceIndex, msg.BlockOffset);
        if (_requestTracker.TryRemovePeerRequest(peer, key, out var r))
        {
            RemoveBlockRequest(r.PieceIndex, r.Offset, peer);
        }

        // The peer has gone back on an offer. Keeping it would have us ask for the same piece again and
        // be refused again, for as long as the connection lasts. Which set it came from depends on
        // whether we are choked: allowed-fast is what may be requested while choked, suggested is a hint
        // that only applies once unchoked.
        peer.WithdrawOfferedPiece(msg.PieceIndex, fromAllowedFast: peer.PeerChoking);

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

    /// <summary>
    /// Serves a block a peer asked for, or refuses it.
    ///
    /// <para>
    /// Every refusal names its reason. This path used to be entirely silent, which made "why did we
    /// never upload to anyone?" unanswerable from a log - the question could not even be narrowed to
    /// whether requests were arriving. Refusals are rare on a healthy connection, so Debug is the right
    /// level; if they are not rare, that is the finding.
    /// </para>
    /// </summary>
    public async Task BlockRequestedAsync(PeerCommunication peer, PeerMessage msg)
    {
        string? rejectReason = null;

        if (peer.AmChoking && !peer.IsAllowedFast(msg.PieceIndex))
        {
            rejectReason = "we are choking this peer and the piece is not allowed-fast";
        }
        else if (!_torrent.Pieces.HasPiece(msg.PieceIndex))
        {
            rejectReason = "we do not have that piece";
        }
        // BEP 16: In superseed mode, only allow requests for assigned pieces
        else if (!_torrent.SuperSeedManager.ShouldAllowRequest(peer, msg.PieceIndex))
        {
            rejectReason = "super-seed mode has not assigned that piece to this peer";
        }
        else if (msg.BlockLength <= 0 || msg.BlockLength > BlockSize)
        {
            rejectReason = "requested length is out of range";
        }
        else if (msg.BlockOffset < 0)
        {
            rejectReason = "requested offset is negative";
        }
        else if (!IsValidUploadRequest(msg))
        {
            rejectReason = "request does not describe a real block of that piece";
        }

        if (rejectReason is not null)
        {
            Logger.LogDebug(
                "Refused request {PieceIndex}:{Offset} ({Length}B) from {PeerName}: {Reason}",
                msg.PieceIndex, msg.BlockOffset, msg.BlockLength, peer.Name, rejectReason);

            await SendRejectAsync(peer, msg).ConfigureAwait(false);
            return;
        }

        var item = new UploadQueueItem(msg.PieceIndex, msg.BlockOffset, msg.BlockLength);
        if (!_uploadQueueManager.TryEnqueue(peer, item))
        {
            // Upload queue full — reject so peer can retry
            Logger.LogDebug(
                "Refused request {PieceIndex}:{Offset} ({Length}B) from {PeerName}: upload queue is full",
                msg.PieceIndex, msg.BlockOffset, msg.BlockLength, peer.Name);

            await SendRejectAsync(peer, msg).ConfigureAwait(false);
        }
    }

    private static Task SendRejectAsync(PeerCommunication peer, PeerMessage msg)
    {
        return peer.SendRejectAsync(new BlockRequest
        {
            PieceIndex = msg.PieceIndex,
            Offset = msg.BlockOffset,
            Length = msg.BlockLength
        });
    }

    private async Task ExecuteUploadItemAsync(PeerCommunication peer, UploadQueueItem item, CancellationToken ct)
    {
        Block? block = null;
        try
        {
            long pieceOffset = item.PieceIndex * _torrent.InfoFile.Info.PieceSize;
            long globalOffset = pieceOffset + item.Offset;

            block = new Block(item.PieceIndex, item.Offset, item.Length);
            await _torrent.FilesInternal.ReadAsync(globalOffset, block.Buffer.AsMemory(0, item.Length), ct).ConfigureAwait(false);

            var response = new PeerMessage(MessageId.Piece)
            {
                PieceIndex = item.PieceIndex,
                BlockOffset = item.Offset,
                PooledBlock = block
            };

            await peer.SendMessageAsync(response).ConfigureAwait(false);
            block = null;

            Uploader.AddUploaded(item.Length);
            peer.AddUploaded(item.Length);
        }
        catch (OperationCanceledException) { block?.Dispose(); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to fulfil request from {RemoteEndPoint}", peer.RemoteEndPoint);
            block?.Dispose();

            await RetractUnreadablePieceAsync(peer, item).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// BEP 54: a piece we advertised turned out to be unreadable, so tell the swarm.
    ///
    /// <para>
    /// The usual cause is the file having been moved or deleted from under a seeding client, which used
    /// to leave the requester waiting on a block that would never arrive and every later request for the
    /// piece failing the same silent way. A retraction lets peers source the piece elsewhere
    /// immediately, and the reject stops this particular request hanging.
    /// </para>
    ///
    /// <para>
    /// A transient I/O error costs peers a re-source they did not strictly need. That is the better
    /// error: continuing to advertise data we cannot read is indistinguishable, from the outside, from
    /// being an unreliable peer.
    /// </para>
    /// </summary>
    private async Task RetractUnreadablePieceAsync(PeerCommunication peer, UploadQueueItem item)
    {
        try
        {
            // Not withdrawn from _torrent.Pieces: the completion counters behind it only ever count
            // upward, so clearing a piece there would leave progress and "finished" state inconsistent.
            // A recheck is the correct way to rebuild the local bitfield, hence the warning.
            Logger.LogWarning(
                "Piece {PieceIndex} could not be read while serving it; retracting it from peers (BEP 54). " +
                "Run a recheck to rebuild local piece state.",
                item.PieceIndex);

            await peer.SendRejectAsync(new BlockRequest
            {
                PieceIndex = item.PieceIndex,
                Offset = item.Offset,
                Length = item.Length
            }).ConfigureAwait(false);

            foreach (var other in _torrent.PeersInternal.GetConnectedPeersInternal())
            {
                await other.LtDontHave.SendAsync(item.PieceIndex).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Best effort: the read already failed, and failing to announce that must not cascade.
            Logger.LogDebug(ex, "Failed to retract unreadable piece {PieceIndex}", item.PieceIndex);
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
                Logger.LogError(ex, "Error in immediate peer evaluation for {RemoteEndPoint}", peer.RemoteEndPoint);
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

    public async Task RequestBlocksAsync(PeerCommunication peer, bool immediate)
    {
        await EvaluateNextRequestsAsync(peer, immediate).ConfigureAwait(false);
    }

    public void UnregisterPeerAvailability(PeerCommunication peer)
    {
        _piecePicker.UnregisterPeerAvailability(peer);
        _requestTracker.RemovePeer(peer);
        _uploadQueueManager.RemovePeer(peer);
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
                Logger.LogTrace("REQUEST STARVATION: No pending requests! peers={PeerCount}, unchoked={Unchoked}, activePieces={ActivePieces}, finished={Finished}", peerCount, unchokedPeers, _pieceStateManager.Count, _torrent.Finished);
            }
            else if (isStalled)
            {
                Logger.LogTrace("REQUEST STALL: Oldest request is {Age}ms old! pendingRequests={Pending}, peersWithRequests={PeersWithRequests}", oldestRequestAgeMs, totalPendingRequests, peersWithRequests);
            }

            Logger.LogTrace("Transfer status: peers={PeerCount}, unchoked={Unchoked}, peersWithRequests={PeersWithRequests}, pendingRequests={Pending}, activePieces={ActivePieces}/{MaxActive}, blockIndex={BlockIndex}, oldestReq={OldestReq}ms, endGame={EndGame}",
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
            catch (Exception ex) { Logger.LogError(ex, "RequestBlocks error for {RemoteEndPoint}", peer.RemoteEndPoint); }
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
        await _blockProcessor.HandleWebSeedBlockReceivedAsync(block, _stoppingToken).ConfigureAwait(false);
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
                Logger.LogWarning(ex, "Error during FileTransfer.Stop()");
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
                        Logger.LogTrace(ex, "Background tasks did not complete within timeout during disposal");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Error waiting for background tasks");
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
                        Logger.LogTrace(ex, "Overflow tasks did not complete within timeout during disposal");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Error waiting for overflow tasks");
            }

            await _uploadQueueManager.DisposeAsync().ConfigureAwait(false);
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
                    Logger.LogError(ex, "Error processing block from {RemoteEndPoint}", item.Peer.RemoteEndPoint);
                    // Ensure block is disposed even on error
                    item.Block.Dispose();
                    // And free its request slot so the block gets re-requested promptly
                    ReleasePendingRequest(item.Peer, item.Block);
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

        Logger.LogWarning("Piece processing queue full - forcing immediate processing for piece {PieceIndex}. Disk I/O may be bottlenecked", pieceToProcess.Index);
        var task = ProcessPieceWithOverflowLimitAsync(pieceToProcess, _stoppingToken);
        _overflowTasks.TryAdd(task, 0);

        _ = task.ContinueWith(t =>
        {
            _overflowTasks.TryRemove(t, out _);
            if (t.IsFaulted)
            {
                Logger.LogError(t.Exception, "Overflow piece processing failed");
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

        Logger.LogWarning("Piece processing queue full - piece {PieceIndex} waiting for queue space", pieceToProcess.Index);
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
            Logger.LogError("Overflow processing timeout for piece {PieceIndex} - system severely overloaded", pieceToProcess.Index);
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
                    catch (StorageException ex) when (!ex.IsRecoverable)
                    {
                        // Disk full or permanently failed file: retrying would re-download this
                        // piece against a broken disk forever. Surface the error and stop.
                        pieceToProcess.Reset();
                        _ = HandleFatalStorageErrorAsync(ex).ContinueWith(
                            t => Logger.LogError(t.Exception, "Fatal storage error handler faulted"),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted,
                            TaskScheduler.Default);
                        return;
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
                        Logger.LogWarning("Piece {PieceIndex} write failed, state reset for retry", pieceToProcess.Index);
                    }
                    else if (outcome.HashFailed)
                    {
                        // Read who supplied it before resetting, because Reset clears the contributor
                        // list. Taking them in the other order meant the loop below iterated an empty
                        // set every time, so in the whole life of this code no peer was ever struck for
                        // bad data and none was ever dropped for it - the accounting existed, ran, and
                        // could not have had an effect.
                        var contributors = pieceToProcess.Contributors.ToArray();
                        bool soleSupplier = contributors.Length == 1;

                        pieceToProcess.RecordHashFailure();
                        pieceToProcess.Reset();

                        Logger.LogWarning(
                            "Piece {PieceIndex} failed hash after {Failures} attempt(s), will be retried",
                            pieceToProcess.Index,
                            pieceToProcess.HashFailures);

                        foreach (var p in contributors)
                        {
                            p.Strikes++;

                            // Counted against the address, not this connection. Strikes on the
                            // connection object are lost the moment the peer reconnects, so the same
                            // source could otherwise keep feeding bad data indefinitely.
                            bool refuse = _torrent.PeersInternal.RecordHashFailure(p);

                            // A piece nobody else contributed to leaves no doubt, so there is nothing
                            // to accumulate evidence for. This is what the retry restriction below is
                            // arranged to produce: after a piece has failed once it is asked of one
                            // peer at a time, which turns the next failure into an answer.
                            if (soleSupplier || refuse)
                            {
                                Logger.LogWarning(
                                    soleSupplier
                                        ? "Dropping peer {RemoteEndPoint} - it alone supplied a piece that failed its hash"
                                        : "Dropping peer {RemoteEndPoint} - it has contributed to several pieces that failed their hash",
                                    p.RemoteEndPoint);
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
            Logger.LogError(ex, "Error processing piece {PieceIndex}", pieceToProcess.Index);
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
            var selection = _merkleHashRequestCoordinator.SelectV2Peer(
                request,
                _torrent.PeersInternal.GetConnectedPeersInternal(),
                peer => peer.RemoteSupportsV2 && peer.PeerPieces.HasPiece(pieceIndex),
                _timeProvider.GetUtcNow());

            if (selection.Status == MerkleHashRequestSelectionStatus.Selected && selection.Peer != null && selection.RequestKey != null && request != null)
            {
                _ = selection.Peer.SendHashRequestAsync(request.PiecesRoot, request.BaseLayer, request.Index, request.Length, request.ProofLayers)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _merkleHashRequestCoordinator.CompleteFailedV2Request(selection.RequestKey);
                            Logger.LogDebug(t.Exception, "BEP 52: Failed to request piece layer for piece {PieceIndex}", pieceIndex);
                        }
                    }, TaskScheduler.Default);
                Logger.LogDebug("BEP 52: Requested piece layer for file root {PiecesRoot} from {RemoteEndPoint}", Convert.ToHexString(request.PiecesRoot), selection.Peer.RemoteEndPoint);
                return;
            }

            if (selection.Status == MerkleHashRequestSelectionStatus.NoPeer)
            {
                Logger.LogDebug("BEP 52: No peers available to request hashes for piece {PieceIndex}", pieceIndex);
            }

            return;
        }

        var bep30Selection = MerkleHashRequestCoordinator.SelectBep30Peer(
            _torrent.PeersInternal.GetConnectedPeersInternal(),
            peer => peer.UtHashPiece?.RemoteMessageId.HasValue == true && peer.PeerPieces.HasPiece(pieceIndex));

        if (bep30Selection.Status == MerkleHashRequestSelectionStatus.Selected && bep30Selection.Peer != null)
        {
            bep30Selection.Peer.UtHashPiece!.RequestHashes(pieceIndex);
            Logger.LogDebug("BEP 30: Requested hashes for piece {PieceIndex} from {RemoteEndPoint}", pieceIndex, bep30Selection.Peer.RemoteEndPoint);
            return;
        }

        Logger.LogDebug("BEP 30: No peers available to request hashes for piece {PieceIndex}", pieceIndex);
    }

    private async Task RunBackgroundTaskAsync(Func<CancellationToken, Task> taskFunc, string taskName)
    {
        int restartCount = 0;
        while (!_stoppingToken.IsCancellationRequested)
        {
            try
            {
                await taskFunc(_stoppingToken).ConfigureAwait(false);
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
                    Logger.LogError(ex, "CRITICAL: Background task '{TaskName}' failed {RestartCount} times, giving up", taskName, restartCount);
                    // Alert the system about the failure
                    _torrent.FireErrorEvent(new TorrentException($"Background task '{taskName}' failed after {restartCount} attempts.", _torrent.Hash, ex));
                    _torrent.Alerts.TorrentAlert(AlertId.TorrentInterrupted, _torrent);
                    break;
                }

                Logger.LogWarning(ex, "Background task '{TaskName}' failed (attempt {RestartCount}/{MaxRestarts}), restarting in 1s", taskName, restartCount, MaxBackgroundTaskRestarts);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), _timeProvider, _stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
