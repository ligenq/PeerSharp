using System.Threading.Channels;
using RtcForge;

namespace PeerSharp.WebTorrent;

internal sealed class WebTorrentDataChannelStream : Stream
{
    private readonly IWebRtcDataChannel _channel;
    private readonly Channel<byte[]> _incomingFrames = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pumpTask;
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private AtomicDisposal _disposal = new();

    public WebTorrentDataChannelStream(IWebRtcDataChannel channel)
    {
        _channel = channel;
    }

    public void Start()
    {
        _pumpTask ??= Task.Run(PumpMessagesAsync);
    }

    private bool IsDisposed => _disposal.IsDisposed;

    private async Task PumpMessagesAsync()
    {
        try
        {
            await foreach (var message in _channel.Messages.WithCancellation(_pumpCts.Token).ConfigureAwait(false))
            {
                _incomingFrames.Writer.TryWrite(message.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during stream disposal.
        }
        finally
        {
            _incomingFrames.Writer.TryComplete();
        }
    }

    public override bool CanRead => !IsDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !IsDisposed;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("WebTorrent data channel streams support asynchronous reads only.");
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        while (true)
        {
            if (_currentBuffer != null)
            {
                int remaining = _currentBuffer.Length - _currentOffset;
                int toCopy = Math.Min(remaining, buffer.Length);
                _currentBuffer.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
                _currentOffset += toCopy;
                if (_currentOffset >= _currentBuffer.Length)
                {
                    _currentBuffer = null;
                    _currentOffset = 0;
                }

                return toCopy;
            }

            if (!await _incomingFrames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_incomingFrames.Reader.TryRead(out var next))
            {
                _currentBuffer = next;
                _currentOffset = 0;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("WebTorrent data channel streams support asynchronous writes only.");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        await _channel.SendAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed())
        {
            _pumpCts.Cancel();
            _incomingFrames.Writer.TryComplete();
            _pumpCts.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            _pumpCts.Cancel();
            _incomingFrames.Writer.TryComplete();

            if (_pumpTask != null)
            {
                try
                {
                    await _pumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during stream disposal.
                }
            }

            _pumpCts.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
