using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PeerSharp.Internals.Transfers;

internal sealed class UploadQueueManager : IAsyncDisposable
{
    private const int MaxQueueDepthPerPeer = 250;

    private readonly ConcurrentDictionary<PeerCommunication, PeerUploadQueue> _queues = new();
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
        var queue = _queues.GetOrAdd(peer, CreateQueue);
        return queue.TryEnqueue(item);
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
        if (_queues.TryRemove(peer, out var queue))
        {
            queue.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var pumpTasks = _queues.Values.Select(q => { q.Dispose(); return q.PumpTask; }).ToArray();
        _queues.Clear();

        if (pumpTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(pumpTasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
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
        var cancelled = new ConcurrentDictionary<(int Piece, int Offset), byte>();
        var peerCts = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
        var pump = RunPumpAsync(peer, channel.Reader, cancelled, peerCts.Token);
        return new PeerUploadQueue(channel, cancelled, pump, peerCts);
    }

    private async Task RunPumpAsync(
        PeerCommunication peer,
        ChannelReader<UploadQueueItem> reader,
        ConcurrentDictionary<(int Piece, int Offset), byte> cancelled,
        CancellationToken token)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                // Check token at the start of every iteration so that already-buffered items
                // are not processed after the peer disconnects (RemovePeer cancels the token).
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (cancelled.ContainsKey((item.PieceIndex, item.Offset)))
                {
                    await peer.SendRejectAsync(item.ToBlockRequest()).ConfigureAwait(false);
                    continue;
                }

                if (peer.AmChoking && !peer.IsAllowedFast(item.PieceIndex))
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
        ConcurrentDictionary<(int Piece, int Offset), byte> cancelled,
        Task pump,
        CancellationTokenSource peerCts) : IDisposable
    {
        private AtomicDisposal _disposal = new();

        public Task PumpTask => pump;

        public bool TryEnqueue(UploadQueueItem item) => channel.Writer.TryWrite(item);

        public void Cancel(int piece, int offset) => cancelled.TryAdd((piece, offset), 0);

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

internal readonly struct UploadQueueItem(int pieceIndex, int offset, int length)
{
    public int PieceIndex => pieceIndex;
    public int Offset => offset;
    public int Length => length;

    public BlockRequest ToBlockRequest() => new() { PieceIndex = PieceIndex, Offset = Offset, Length = Length };
}
