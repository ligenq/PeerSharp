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
    private readonly CancellationToken _pumpToken;
    private readonly Lock _lifetimeLock = new();
    private readonly Func<ValueTask>? _onDisposed;
    private Task? _disposeTask;
    private bool _reading;
    private Task? _pumpTask;
    private IMemoryOwner<byte>? _currentMemoryOwner;
    private ReadOnlyMemory<byte> _currentBuffer;
    private int _currentOffset;
    private int _disposed;

    public WebTorrentDataChannelStream(IWebRtcDataChannel channel, Func<ValueTask>? onDisposed = null)
    {
        _pumpToken = _pumpCts.Token;
        _channel = channel;
        _onDisposed = onDisposed;
    }

    public void Start()
    {
        // Task.Run, not a bare call: Start is synchronous and its only job is to kick off the
        // background pump. Invoking the async method inline runs its prologue - and up to a
        // full channel's worth of message copies - on the caller's thread before returning.
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            _pumpTask ??= Task.Run(PumpMessagesAsync, CancellationToken.None);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    private void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private async Task PumpMessagesAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var message in _channel.Messages.WithCancellation(_pumpCts.Token).ConfigureAwait(false))
            {
                if (message.IsEmpty) continue;
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
        catch (Exception ex) { failure = ex; }
        finally
        {
            _incomingFrames.Writer.TryComplete(failure);
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
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (_reading) throw new InvalidOperationException("A read is already in progress.");
            _reading = true;
        }
        try
        {
            while (true)
            {
                lock (_lifetimeLock)
                {
                    if (IsDisposed) return 0;
                    if (_currentMemoryOwner == null && _incomingFrames.Reader.TryRead(out var next))
                    {
                        _currentMemoryOwner = next;
                        _currentBuffer = next.Memory;
                        _currentOffset = 0;
                    }
                    if (_currentMemoryOwner != null)
                    {
                        int toCopy = Math.Min(_currentBuffer.Length - _currentOffset, buffer.Length);
                        _currentBuffer.Slice(_currentOffset, toCopy).CopyTo(buffer);
                        _currentOffset += toCopy;
                        if (_currentOffset == _currentBuffer.Length)
                        {
                            _currentMemoryOwner.Dispose();
                            _currentMemoryOwner = null;
                            _currentBuffer = default;
                            _currentOffset = 0;
                        }
                        return toCopy;
                    }
                }
                if (!await _incomingFrames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            }
        }
        finally { lock (_lifetimeLock) { _reading = false; } }
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pumpToken);
        for (int offset = 0; offset < buffer.Length;)
        {
            int length = Math.Min(16 * 1024, buffer.Length - offset);
            await _channel.SendAsync(buffer.Slice(offset, length), linked.Token).ConfigureAwait(false);
            offset += length;
        }
    }

    private Task BeginDispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposeTask != null) return _disposeTask;
            MarkDisposed();
            _pumpCts.Cancel();
            _incomingFrames.Writer.TryComplete();
            _currentMemoryOwner?.Dispose();
            _currentMemoryOwner = null;
            _currentBuffer = default;
            while (_incomingFrames.Reader.TryRead(out var owner)) owner.Dispose();
            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_pumpTask != null) await _pumpTask.ConfigureAwait(false);
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _pumpCts.Dispose();
            if (_onDisposed != null) await _onDisposed().ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ = BeginDispose().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try { await BeginDispose().ConfigureAwait(false); }
        finally { await base.DisposeAsync().ConfigureAwait(false); }
        GC.SuppressFinalize(this);
    }
}
