using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;

namespace PeerSharp.Internals.Network;

/// <summary>
/// A stream over a connected peer socket that reports the end of the connection - however it ended -
/// as end of input rather than as an exception.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NetworkStream"/> can only say that a peer reset the connection, or that we closed it under
/// a pending read, by throwing. Both are the ordinary end of a peer connection: swarms are mostly
/// strangers who hang up without ceremony, and every connection we close ourselves has a read waiting
/// on it. Each one was a first-chance exception, and so a round trip to any attached debugger.
/// </para>
/// <para>
/// This does what <see cref="Framework.SocketConnect"/> does for connecting: it drives the socket through
/// <see cref="SocketAsyncEventArgs"/>, which reports failures as a <see cref="SocketError"/> value. A
/// read that fails returns zero, and <see cref="EndedBy"/> keeps the reason for anyone who wants it.
/// Every reader above already treats end of input and an I/O error alike, by closing the connection.
/// </para>
/// <para>
/// Cancelling a read gives up on the connection: the socket is closed, which ends the read, and the
/// read returns zero rather than throwing. Nothing reads from a peer and then carries on with the same
/// connection after giving up on a read, and a cancelled socket receive leaves the socket in no state
/// to carry on with anyway. A failed write still throws, because a write cannot report failure any
/// other way and must not pretend the bytes went out; it also closes the socket, so the read side ends
/// the connection promptly.
/// </para>
/// </remarks>
internal sealed class SocketStream : Stream
{
    private readonly Socket _socket;
    private readonly bool _ownsSocket;
    private readonly SocketOperationEventArgs _receive = new();
    private readonly SocketOperationEventArgs _send = new();
    private AtomicDisposal _disposal = new();
    private int _aborted;
    private int _endedBy = (int)SocketError.Success;

    /// <param name="socket">A connected socket.</param>
    /// <param name="ownsSocket">
    /// Whether disposing this stream closes the socket. Cancelling a read closes it either way.
    /// </param>
    public SocketStream(Socket socket, bool ownsSocket = true)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _ownsSocket = ownsSocket;
    }

    /// <summary>
    /// Why reads stopped: <see cref="SocketError.Success"/> while the connection is open or after the peer
    /// closed it cleanly, otherwise the socket's reason, such as <see cref="SocketError.ConnectionReset"/>.
    /// </summary>
    public SocketError EndedBy => (SocketError)Volatile.Read(ref _endedBy);

    public override bool CanRead => !IsDisposed;

    public override bool CanSeek => false;

    public override bool CanWrite => !IsDisposed;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private bool IsDisposed => _disposal.IsDisposed;

    private bool IsAborted => Volatile.Read(ref _aborted) != 0;

    /// <summary>
    /// Closes the connection, ending a pending read as end of input. For an owner that has given up on the
    /// connection - a handshake that ran out of time, a read that was cancelled.
    /// </summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            _socket.Dispose();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (IsDisposed || IsAborted)
        {
            return 0;
        }

        using var giveUp = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((SocketStream)state!).Abort(), this)
            : default;

        int received;
        try
        {
            received = await _receive.ReceiveAsync(_socket, buffer).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed between the check above and the receive being issued.
            return 0;
        }

        if (received < 0)
        {
            Interlocked.CompareExchange(ref _endedBy, (int)_receive.SocketError, (int)SocketError.Success);
            return 0;
        }

        return received;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (IsDisposed || IsAborted)
        {
            return 0;
        }

        int received = _socket.Receive(buffer, SocketFlags.None, out var error);
        if (error != SocketError.Success)
        {
            Interlocked.CompareExchange(ref _endedBy, (int)error, (int)SocketError.Success);
            return 0;
        }

        return received;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var giveUp = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((SocketStream)state!).Abort(), this)
            : default;

        while (!buffer.IsEmpty)
        {
            if (IsDisposed || IsAborted)
            {
                throw new IOException("The connection was closed before everything was written.");
            }

            int sent = await _send.SendAsync(_socket, buffer).ConfigureAwait(false);
            if (sent <= 0)
            {
                var error = sent < 0 ? _send.SocketError : SocketError.ConnectionAborted;
                Abort();
                throw new IOException("The connection was closed before everything was written.", new SocketException((int)error));
            }

            buffer = buffer[sent..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int sent = _socket.Send(buffer, SocketFlags.None, out var error);
            if (error != SocketError.Success || sent <= 0)
            {
                Abort();
                throw new IOException("The connection was closed before everything was written.", new SocketException((int)error));
            }

            buffer = buffer[sent..];
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _disposal.MarkDisposed())
        {
            if (_ownsSocket)
            {
                Abort();
            }

            // Deferred by the runtime until an operation still in flight completes.
            _receive.Dispose();
            _send.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// One reusable socket operation, awaitable without allocating. Completes with the byte count, or -1
    /// when the socket reported an error, which is left in <see cref="SocketAsyncEventArgs.SocketError"/>.
    /// </summary>
    private sealed class SocketOperationEventArgs : SocketAsyncEventArgs, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _completion = new() { RunContinuationsAsynchronously = true };

        public ValueTask<int> ReceiveAsync(Socket socket, Memory<byte> buffer)
        {
            _completion.Reset();
            SetBuffer(buffer);

            // False means it finished before returning, and no completion will be raised.
            return socket.ReceiveAsync(this)
                ? new ValueTask<int>(this, _completion.Version)
                : new ValueTask<int>(Outcome());
        }

        public ValueTask<int> SendAsync(Socket socket, ReadOnlyMemory<byte> buffer)
        {
            _completion.Reset();
            SetBuffer(MemoryMarshal.AsMemory(buffer));
            return socket.SendAsync(this)
                ? new ValueTask<int>(this, _completion.Version)
                : new ValueTask<int>(Outcome());
        }

        public int GetResult(short token) => _completion.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _completion.OnCompleted(continuation, state, token, flags);

        protected override void OnCompleted(SocketAsyncEventArgs e) => _completion.SetResult(Outcome());

        private int Outcome() => SocketError == SocketError.Success ? BytesTransferred : -1;
    }
}
