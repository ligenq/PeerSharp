using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Internals.Peers;

/// <summary>
/// Applies the configured download and upload rate limits to one peer connection by reserving quota
/// from the bandwidth manager before any bytes cross the wire.
///
/// <para>
/// This logic used to live inside <see cref="EncryptedStream"/>, which meant it only ran on connections
/// that negotiated encryption. Plaintext peers - what you get whenever the remote declines encryption,
/// and always under <see cref="Encryption.Refuse"/> - were not limited at all, so a configured ceiling
/// silently did nothing on those connections. Limiting is not an encryption concern, so it lives in its
/// own layer that wraps the socket unconditionally, with encryption layered above it when present.
/// </para>
///
/// <para>
/// Byte accounting is deliberately on the wire side: this sits below encryption, so what it counts is
/// what the network actually carries.
/// </para>
/// </summary>
internal sealed class RateLimitedStream : Stream
{
    /// <summary>Largest read or write issued to the inner stream, so one call cannot hog a reservation.</summary>
    private const int ChunkSize = ProtocolConstants.BlockSize;

    private readonly IBandwidthManager _bandwidthManager;
    private readonly string[] _downloadChannels;
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly string[] _uploadChannels;
    private readonly IBandwidthUser _downloadUser;
    private readonly IBandwidthUser _uploadUser;
    private AtomicDisposal _disposal = new();
    private readonly Lock _reservationLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _operations;

    // Returned to the bandwidth manager on disposal so a dropped connection cannot leak quota.
    private int _reservedDownloadBandwidth;
    private int _reservedUploadBandwidth;

    public RateLimitedStream(
        Stream inner,
        IBandwidthUser user,
        IBandwidthManager bandwidthManager,
        string[] downloadChannels,
        string[] uploadChannels,
        bool leaveInnerOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _downloadUser = new DirectionUser(user, "download");
        _uploadUser = new DirectionUser(user, "upload");
        _bandwidthManager = bandwidthManager;
        _downloadChannels = downloadChannels;
        _uploadChannels = uploadChannels;
        _leaveInnerOpen = leaveInnerOpen;
    }

    // Guards against a permanent quota leak if an exception leaves this unreachable without disposal.
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    ~RateLimitedStream()
    {
        Dispose(false);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();

    /// <summary>
    /// Synchronous reads bypass the limiter: the bandwidth manager is asynchronous by nature, and
    /// blocking on it here would deadlock the very timer that replenishes quota. The peer loops are
    /// fully asynchronous, so this path is only reached by tests and diagnostics.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_reservationLock)
        {
            if (_disposal.IsDisposed) return 0;
            _operations++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await ReadCoreAsync(buffer, linked, cancellationToken).ConfigureAwait(false); }
            finally { _readGate.Release(); }
        }
        catch (OperationCanceledException) when (_disposal.IsDisposed && !cancellationToken.IsCancellationRequested) { return 0; }
        finally { EndOperation(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationTokenSource quotaLifetime, CancellationToken ioCancellationToken)
    {
        var cancellationToken = quotaLifetime.Token;
        if (_disposal.IsDisposed)
        {
            return 0;
        }

        int toRead = Math.Min(buffer.Length, ChunkSize);
        if (toRead == 0) return 0;

        if (_reservedDownloadBandwidth < toRead)
        {
            // Request in batches rather than per-read, so a fast connection does not take the manager's
            // lock once per handful of bytes.
            int needed = toRead - _reservedDownloadBandwidth;
            int requestAmount = Math.Max(needed, ProtocolConstants.DownloadBatchSize);

            int granted = await _bandwidthManager.RequestBandwidthAsync(
                _downloadUser,
                requestAmount,
                1,
                _downloadChannels,
                cancellationToken).ConfigureAwait(false);

            if (granted <= 0) throw new IOException("No download bandwidth was granted, so this read cannot proceed.");
            lock (_reservationLock)
            {
                if (_disposal.IsDisposed)
                {
                    _bandwidthManager.ReturnBandwidth(granted, _downloadChannels);
                    return 0;
                }
                _reservedDownloadBandwidth += granted;
            }
        }
        int canRead;
        lock (_reservationLock)
        {
            if (_disposal.IsDisposed) return 0;
            canRead = Math.Min(toRead, _reservedDownloadBandwidth);
            // In-flight I/O owns this quota. Disposal may refund only the unused remainder.
            _reservedDownloadBandwidth -= canRead;
        }

        int read;
        try
        {
            // A failed read may already have consumed bytes, so its in-flight quota is not refunded.
            read = await _inner.ReadAsync(buffer[..canRead], ioCancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed while waiting for quota. Zero is how a stream reports end of input, and every
            // caller already treats it as the connection being gone.
            return 0;
        }
        catch (IOException)
        {
            // The peer reset the connection, or we closed it under a pending read. Either way it is
            // gone, and how it went makes no difference to anything above: every reader treats end of
            // input and an I/O error alike, by closing. Letting it through cost a rethrow at each of
            // the layers stacked on this one - encryption, the handshake prefix and the pipe reader -
            // for the most ordinary event in a swarm, several times a second.
            return 0;
        }

        lock (_reservationLock)
        {
            int unused = canRead - read;
            if (_disposal.IsDisposed) _bandwidthManager.ReturnBandwidth(unused, _downloadChannels);
            else _reservedDownloadBandwidth += unused;
        }

        return read;
    }



    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>See <see cref="Read(byte[], int, int)"/> for why this is unlimited.</summary>
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <summary>
    /// Writes the whole buffer or throws. It must never return having written less.
    ///
    /// <para>
    /// This used to give up quietly in three places - disposal mid-loop, a zero bandwidth grant, and
    /// an <see cref="ObjectDisposedException"/> from the socket - each returning normally with bytes
    /// left unsent. That breaks the <see cref="Stream"/> contract, and on an encrypted connection it
    /// corrupts the peer.
    /// <see cref="EncryptedStream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> sits directly
    /// above this and has already advanced RC4 over the <em>entire</em> buffer before handing it down,
    /// because a stream cipher has to encrypt before it can write. Dropping the tail leaves the remote's
    /// inbound keystream permanently offset by exactly the number of bytes we swallowed, and every
    /// message it reads from us afterwards is garbage.
    /// </para>
    ///
    /// <para>
    /// Throwing closes the connection, which is the honest outcome: a desynchronised peer is strictly
    /// worse than a closed one, because it looks alive while being unable to exchange anything.
    /// </para>
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_reservationLock)
        {
            if (_disposal.IsDisposed) throw new IOException("The connection was disposed before the write.");
            _operations++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await WriteCoreAsync(buffer, linked.Token).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        }
        finally { EndOperation(); }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        int sent = 0;
        while (sent < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_disposal.IsDisposed)
            {
                throw new IOException(
                    "The connection was closed part way through a write. See the note on partial writes below.");
            }

            int remaining = Math.Min(buffer.Length - sent, ChunkSize);

            if (_reservedUploadBandwidth < remaining)
            {
                int needed = remaining - _reservedUploadBandwidth;
                int requestAmount = Math.Max(needed, ProtocolConstants.UploadBatchSize);

                int granted = await _bandwidthManager.RequestBandwidthAsync(
                    _uploadUser,
                    requestAmount,
                    1,
                    _uploadChannels,
                    cancellationToken).ConfigureAwait(false);

                if (granted <= 0)
                {
                    // No quota and none coming. Nothing was reserved, so there is nothing to return -
                    // but we cannot simply stop, because the bytes have already been encrypted. See
                    // the note on partial writes below.
                    throw new IOException("No upload bandwidth was granted, so this write cannot complete.");
                }

                lock (_reservationLock)
                {
                    if (_disposal.IsDisposed)
                    {
                        _bandwidthManager.ReturnBandwidth(granted, _uploadChannels);
                        throw new IOException("The connection was disposed while waiting for upload quota.");
                    }
                    _reservedUploadBandwidth += granted;
                }
            }

            int toSend;
            lock (_reservationLock)
            {
                if (_disposal.IsDisposed) throw new IOException("The connection was disposed part way through a write.");
                toSend = Math.Min(remaining, _reservedUploadBandwidth);
                _reservedUploadBandwidth -= toSend;
            }

            try
            {
                await _inner.WriteAsync(buffer.Slice(sent, toSend), cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                // The connection was torn down while this write was waiting for quota. Nothing is left
                // to write to, and the peer is already being closed by whoever disposed it - but this
                // still has to be reported rather than swallowed. See the note on partial writes below.
                //
                // Reserving bandwidth mid-write is what makes this reachable at all: the gap between
                // deciding to send and sending grows with how throttled the connection is.
                throw new IOException("The connection was disposed part way through a write.", ex);
            }

            sent += toSend;
        }
    }



    private sealed class DirectionUser(IBandwidthUser user, string direction) : IBandwidthUser
    {
        public string Name => $"{user.Name}/{direction}";
        public void AssignBandwidth(int amount) { /* Requests complete through their tasks. */ }
    }

    private void EndOperation()
    {
        lock (_reservationLock)
        {
            _operations--;
            if (_operations == 0 && _disposal.IsDisposed) _lifetimeCts.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_reservationLock)
        {
            if (_disposal.MarkDisposed())
            {
                _lifetimeCts.Cancel();
                _bandwidthManager.ReturnBandwidth(_reservedDownloadBandwidth, _downloadChannels);
                _bandwidthManager.ReturnBandwidth(_reservedUploadBandwidth, _uploadChannels);
                _reservedDownloadBandwidth = 0;
                _reservedUploadBandwidth = 0;
                if (_operations == 0) _lifetimeCts.Dispose();
                if (disposing && !_leaveInnerOpen) _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
