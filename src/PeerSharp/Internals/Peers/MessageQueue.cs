using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

internal sealed class MessageQueue
{
    private readonly Channel<PeerMessage> _queue;

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
        _queue.Writer.TryComplete();
    }
}
