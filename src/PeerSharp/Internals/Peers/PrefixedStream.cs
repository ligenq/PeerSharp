using PeerSharp.Internals.Framework;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// Serves a block of already-read bytes before continuing from the underlying stream.
///
/// <para>
/// A peer usually sends its handshake and the messages that follow in the same TCP segment, so reading
/// the handshake tends to pull the beginning of the next message along with it. Those extra bytes have
/// left the socket and cannot be put back, so whatever reads the message stream afterwards has to see
/// them first or the stream begins mid-message - which surfaces as a nonsense length prefix on the very
/// first decode. Putting them in front of the stream rather than decoding them separately means a
/// message straddling the boundary needs no special handling.
/// </para>
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private AtomicDisposal _disposal = new();
    private int _prefixConsumed;

    /// <param name="prefix">Bytes to serve before reading from <paramref name="inner"/>.</param>
    /// <param name="inner">The stream to continue from once the prefix is exhausted.</param>
    /// <param name="leaveInnerOpen">
    /// Whether the wrapped stream outlives this one. The connection stream is owned by
    /// PeerCommunication, whose CleanupResourcesAsync disposes it to close the connection, so callers
    /// there pass true - matching how EncryptedStream and RateLimitedStream declare the same thing.
    /// </param>
    public PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner, bool leaveInnerOpen = false)
    {
        _prefix = prefix;
        _inner = inner;
        _leaveInnerOpen = leaveInnerOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int remaining = _prefix.Length - _prefixConsumed;
        if (remaining > 0)
        {
            // Serve the prefix on its own rather than topping up from the socket: a short read is
            // legitimate, and waiting for more would stall a peer that is waiting on us.
            int toCopy = Math.Min(remaining, buffer.Length);
            _prefix.Slice(_prefixConsumed, toCopy).CopyTo(buffer);
            _prefixConsumed += toCopy;
            return toCopy;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int remaining = _prefix.Length - _prefixConsumed;
        if (remaining > 0)
        {
            int toCopy = Math.Min(remaining, count);
            _prefix.Slice(_prefixConsumed, toCopy).Span.CopyTo(buffer.AsSpan(offset, toCopy));
            _prefixConsumed += toCopy;
            return toCopy;
        }

        return _inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing && !_leaveInnerOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed() && !_leaveInnerOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
