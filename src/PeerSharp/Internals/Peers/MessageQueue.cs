using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

internal sealed class MessageQueue
{
    private readonly Channel<PeerMessage> _queue;
    private readonly TimeProvider _timeProvider;
    private int _completed;

    public MessageQueue(int capacity)
        : this(capacity, TimeProvider.System)
    {
    }

    /// <param name="capacity">Messages held before a sender has to wait.</param>
    /// <param name="timeProvider">Times how long <see cref="TryEnqueueWithinAsync"/> waits for room.</param>
    public MessageQueue(int capacity, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _queue = Channel.CreateBounded<PeerMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int Count => _queue.Reader.Count;

    /// <summary>
    /// Whether the queue has been closed for writing.
    ///
    /// <para>
    /// <see cref="TryEnqueue"/> returns false for a full queue and a closed one alike, so without this
    /// the only way to tell them apart was to wait on the channel and let it throw. Every
    /// message still in flight when a peer disconnects took that path, which made an ordinary and
    /// already-known state cost an exception per message.
    /// </para>
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool TryEnqueue(PeerMessage msg)
    {
        return _queue.Writer.TryWrite(msg);
    }

    /// <summary>
    /// Queues <paramref name="msg"/>, waiting up to <paramref name="timeout"/> for room. Running out of time,
    /// or the queue closing, is reported as false rather than thrown: a peer that stops reading fills its
    /// queue as a matter of course, and a cancelled wait could only say so with an exception. The caller
    /// still owns the message when this returns false.
    /// </summary>
    public async ValueTask<bool> TryEnqueueWithinAsync(PeerMessage msg, TimeSpan timeout)
    {
        long started = _timeProvider.GetTimestamp();
        while (!_queue.Writer.TryWrite(msg))
        {
            var remaining = timeout - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || IsCompleted)
            {
                return false;
            }

            // Uncancelled, so it can only end by room appearing or the queue closing - both quietly.
            var room = _queue.Writer.WaitToWriteAsync(CancellationToken.None).AsTask();
            if (!room.IsCompleted)
            {
                using var stopTimer = new CancellationTokenSource();
                var timer = Task.Delay(remaining, _timeProvider, stopTimer.Token);
                var first = await Task.WhenAny(room, timer).ConfigureAwait(false);
                await stopTimer.CancelAsync().ConfigureAwait(false);
                if (first != room)
                {
                    return false;
                }
            }

            if (!await room.ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        return _queue.Reader.WaitToReadAsync(ct);
    }

    public bool TryDequeue(out PeerMessage msg)
    {
        return _queue.Reader.TryRead(out msg!);
    }

    public void TryComplete()
    {
        Volatile.Write(ref _completed, 1);
        _queue.Writer.TryComplete();
    }
}
