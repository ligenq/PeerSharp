using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

internal sealed class MessageQueue
{
    private readonly Channel<PeerMessage> _queue;
    private int _completed;

    public MessageQueue(int capacity)
    {
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
    /// the only way to tell them apart was to call <see cref="EnqueueAsync"/> and let it throw. Every
    /// message still in flight when a peer disconnects took that path, which made an ordinary and
    /// already-known state cost an exception per message.
    /// </para>
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool TryEnqueue(PeerMessage msg)
    {
        return _queue.Writer.TryWrite(msg);
    }

    public ValueTask EnqueueAsync(PeerMessage msg, CancellationToken ct)
    {
        return _queue.Writer.WriteAsync(msg, ct);
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
