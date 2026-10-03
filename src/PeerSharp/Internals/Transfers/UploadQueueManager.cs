using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PeerSharp.Internals.Transfers;

internal sealed class UploadQueueManager : IAsyncDisposable
{
    /// <summary>
    /// What we will hold from one peer, and exactly what we advertise as <c>reqq</c>. Internal rather
    /// than private so a test can assert the two have not drifted apart.
    /// </summary>
    internal const int MaxQueueDepthPerPeer = ProtocolConstants.MaxOutstandingRequestsPerPeer;

    private readonly ConcurrentDictionary<PeerCommunication, PeerUploadQueue> _queues = new();
    private readonly Lock _queuesLock = new();
    private bool _disposed;
    private readonly Func<PeerCommunication, UploadQueueItem, CancellationToken, Task> _execute;
    private readonly CancellationToken _stopToken;
    private readonly ILogger<UploadQueueManager> _logger;

    public UploadQueueManager(
        Func<PeerCommunication, UploadQueueItem, CancellationToken, Task> execute,
        ILogger<UploadQueueManager> logger,
        CancellationToken stopToken)
    {
        _execute = execute;
        _logger = logger;
        _stopToken = stopToken;
    }

    public bool TryEnqueue(PeerCommunication peer, UploadQueueItem item)
    {
        lock (_queuesLock)
        {
            if (_disposed || _stopToken.IsCancellationRequested) return false;
            if (!_queues.TryGetValue(peer, out var queue))
                _queues[peer] = queue = CreateQueue(peer);
            return queue.TryEnqueue(item);
        }
    }

    public void Cancel(PeerCommunication peer, int piece, int offset)
    {
        if (_queues.TryGetValue(peer, out var queue))
        {
            queue.Cancel(piece, offset);
        }
    }

    public void RemovePeer(PeerCommunication peer)
    {
        lock (_queuesLock)
        {
            if (_queues.TryRemove(peer, out var queue)) queue.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pumpTasks;
        lock (_queuesLock)
        {
            if (_disposed) return;
            _disposed = true;
            pumpTasks = _queues.Values.Select(q => { q.Dispose(); return q.PumpTask; }).ToArray();
            _queues.Clear();
        }

        if (pumpTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(pumpTasks).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { /* shutdown */ }
        }
    }

    private PeerUploadQueue CreateQueue(PeerCommunication peer)
    {
        var channel = Channel.CreateBounded<UploadQueueItem>(new BoundedChannelOptions(MaxQueueDepthPerPeer)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        var waiting = new WaitingRequests();
        var peerCts = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
        var pump = RunPumpAsync(peer, channel.Reader, waiting, peerCts.Token);
        return new PeerUploadQueue(channel, waiting, pump, peerCts);
    }

    private async Task RunPumpAsync(
        PeerCommunication peer,
        ChannelReader<UploadQueueItem> reader,
        WaitingRequests waiting,
        CancellationToken token)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                // Check token at the start of every iteration so that already-buffered items
                // are not processed after the peer disconnects (RemovePeer cancels the token).
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (waiting.Take(item.PieceIndex, item.Offset))
                {
                    await peer.SendRejectAsync(item.ToBlockRequest()).ConfigureAwait(false);
                    continue;
                }

                if (peer.AmChoking && !peer.IsUploadAllowedFast(item.PieceIndex))
                {
                    await peer.SendRejectAsync(item.ToBlockRequest()).ConfigureAwait(false);
                    continue;
                }

                await _execute(peer, item, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload pump failed for {RemoteEndPoint}", peer.RemoteEndPoint);
        }
    }

    private sealed class PeerUploadQueue(
        Channel<UploadQueueItem> channel,
        WaitingRequests waiting,
        Task pump,
        CancellationTokenSource peerCts) : IDisposable
    {
        private AtomicDisposal _disposal = new();

        public Task PumpTask => pump;

        public bool TryEnqueue(UploadQueueItem item) => waiting.TryEnqueue(channel.Writer, item);

        public void Cancel(int piece, int offset) => waiting.Cancel(piece, offset);

        public void Dispose()
        {
            if (!_disposal.MarkDisposed())
            {
                return;
            }

            peerCts.Cancel();
            channel.Writer.TryComplete();
            peerCts.Dispose();
        }
    }
}

/// <summary>
/// The requests a peer has queued with us and not yet been answered, and which of them it has
/// cancelled.
/// </summary>
/// <remarks>
/// A cancel applies to a request still waiting, and to nothing after it. Cancels used to be kept for as
/// long as the connection lasted, so a peer that cancelled a block and later asked for it again - as a
/// streaming client does when it makes room for what its player needs, and any client does when a
/// piece fails its hash - was refused that block every time, and had to get it elsewhere or not at all.
/// A cancel for a request no longer waiting, already sent or never made, is ignored.
/// </remarks>
internal sealed class WaitingRequests
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(int Piece, int Offset), (int Queued, int Cancelled)> _requests = [];

    /// <summary>Records a request only if the channel accepts it, before the pump can take it.</summary>
    public bool TryEnqueue(ChannelWriter<UploadQueueItem> writer, UploadQueueItem item)
    {
        lock (_lock)
        {
            if (!writer.TryWrite(item))
            {
                return false;
            }

            // Take and Cancel use the same lock, so neither can observe an accepted request
            // before it is counted. A refused request leaves earlier cancellations untouched.
            AddCore(item.PieceIndex, item.Offset);
            return true;
        }
    }

    /// <summary>A request has been queued.</summary>
    public void Add(int piece, int offset)
    {
        lock (_lock)
        {
            AddCore(piece, offset);
        }
    }

    private void AddCore(int piece, int offset)
    {
        var counts = _requests.GetValueOrDefault((piece, offset));
        _requests[(piece, offset)] = (counts.Queued + 1, counts.Cancelled);
    }

    /// <summary>The peer cancelled a request for this block: the next one taken off the queue is refused, if one is waiting.</summary>
    public void Cancel(int piece, int offset)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue((piece, offset), out var counts) && counts.Cancelled < counts.Queued)
            {
                _requests[(piece, offset)] = (counts.Queued, counts.Cancelled + 1);
            }
        }
    }

    /// <summary>A request is taken off the queue. True when it was cancelled.</summary>
    public bool Take(int piece, int offset)
    {
        lock (_lock)
        {
            if (!_requests.TryGetValue((piece, offset), out var counts))
            {
                return false;
            }

            if (counts.Queued <= 1)
            {
                _requests.Remove((piece, offset));
            }
            else
            {
                _requests[(piece, offset)] = (counts.Queued - 1, Math.Max(0, counts.Cancelled - 1));
            }

            return counts.Cancelled > 0;
        }
    }
}

internal readonly struct UploadQueueItem(int pieceIndex, int offset, int length)
{
    public int PieceIndex => pieceIndex;
    public int Offset => offset;
    public int Length => length;

    public BlockRequest ToBlockRequest() => new() { PieceIndex = PieceIndex, Offset = Offset, Length = Length };
}
