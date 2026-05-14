using System.Threading.Channels;
using RtcForge;
using System.Buffers;

namespace PeerSharp.WebTorrent.Transport;

internal sealed class WebTorrentDataChannelStream : Stream
{
    private readonly IWebRtcDataChannel _channel;
    private readonly Channel<IMemoryOwner<byte>> _incomingFrames = Channel.CreateBounded<IMemoryOwner<byte>>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pumpTask;
    private IMemoryOwner<byte>? _currentMemoryOwner;
    private ReadOnlyMemory<byte> _currentBuffer;
    private int _currentOffset;
    private int _disposed;

    public WebTorrentDataChannelStream(IWebRtcDataChannel channel)
    {
        _channel = channel;
    }

    public void Start()
    {
        _pumpTask ??= Task.Run(PumpMessagesAsync);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    private bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private async Task PumpMessagesAsync()
    {
        try
        {
            await foreach (var message in _channel.Messages.WithCancellation(_pumpCts.Token).ConfigureAwait(false))
            {
                var owner = MemoryPool<byte>.Shared.Rent(message.Length);
                SlicedMemoryOwner? slicedOwner = null;
                try
                {
                    message.CopyTo(owner.Memory);
                    // Slice it to the exact length of the incoming message.
                    slicedOwner = new SlicedMemoryOwner(owner, message.Length);
                    await _incomingFrames.Writer.WriteAsync(slicedOwner, _pumpCts.Token).ConfigureAwait(false);
                    owner = null!;
                    slicedOwner = null;
                }
                finally
                {
                    if (slicedOwner != null)
                    {
                        slicedOwner.Dispose();
                    }
                    else
                    {
                        owner?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during stream disposal.
        }
        catch (ChannelClosedException)
        {
            // Expected if the stream completes while a producer is waiting for capacity.
        }
        finally
        {
            _incomingFrames.Writer.TryComplete();
        }
    }

    private sealed class SlicedMemoryOwner : IMemoryOwner<byte>
    {
        private readonly IMemoryOwner<byte> _inner;
        private readonly int _length;

        public SlicedMemoryOwner(IMemoryOwner<byte> inner, int length)
        {
            _inner = inner;
            _length = length;
        }

        public Memory<byte> Memory => _inner.Memory[.._length];

        public void Dispose() => _inner.Dispose();
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
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (_currentMemoryOwner != null)
            {
                int remaining = _currentBuffer.Length - _currentOffset;
                int toCopy = Math.Min(remaining, buffer.Length);
                _currentBuffer.Slice(_currentOffset, toCopy).CopyTo(buffer);
                _currentOffset += toCopy;
                if (_currentOffset >= _currentBuffer.Length)
                {
                    _currentMemoryOwner.Dispose();
                    _currentMemoryOwner = null;
                    _currentBuffer = default;
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
                _currentMemoryOwner = next;
                _currentBuffer = next.Memory;
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
        ThrowIfDisposed();
        await _channel.SendAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (MarkDisposed())
        {
            _pumpCts.Cancel();
            _incomingFrames.Writer.TryComplete();

            _currentMemoryOwner?.Dispose();
            while (_incomingFrames.Reader.TryRead(out var owner))
            {
                owner.Dispose();
            }

            _ = _channel.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            _pumpCts.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (MarkDisposed())
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
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

            _currentMemoryOwner?.Dispose();
            while (_incomingFrames.Reader.TryRead(out var owner))
            {
                owner.Dispose();
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
