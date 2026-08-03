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
    private readonly IBandwidthUser _user;
    private AtomicDisposal _disposal = new();

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
        _user = user;
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
        if (_disposal.IsDisposed)
        {
            return 0;
        }

        int toRead = Math.Min(buffer.Length, ChunkSize);

        if (_reservedDownloadBandwidth < toRead)
        {
            // Request in batches rather than per-read, so a fast connection does not take the manager's
            // lock once per handful of bytes.
            int needed = toRead - _reservedDownloadBandwidth;
            int requestAmount = Math.Max(needed, ProtocolConstants.DownloadBatchSize);

            int granted = await _bandwidthManager.RequestBandwidthAsync(
                _user,
                requestAmount,
                1,
                _downloadChannels,
                cancellationToken).ConfigureAwait(false);

            if (granted <= 0)
            {
                // Zero from a read means end of input, and this is not that - the socket is fine, we
                // just have no quota. Reporting EOF here ends the receive loop and drops a healthy
                // peer for a reason the user cannot see.
                throw new IOException("No download bandwidth was granted, so this read cannot proceed.");
            }

            _reservedDownloadBandwidth += granted;
        }

        int canRead = Math.Min(toRead, _reservedDownloadBandwidth);

        int read;
        try
        {
            // Cancellation is not caught here: the reservation stays tracked and is returned on disposal.
            read = await _inner.ReadAsync(buffer[..canRead], cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed while waiting for quota. Zero is how a stream reports end of input, and every
            // caller already treats it as the connection being gone.
            return 0;
        }

        if (read > 0)
        {
            _reservedDownloadBandwidth -= read;
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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
    /// corrupts the peer. <see cref="EncryptedStream.WriteAsync"/> sits directly above this and has
    /// already advanced RC4 over the <em>entire</em> buffer before handing it down, because a stream
    /// cipher has to encrypt before it can write. Dropping the tail therefore leaves the remote's
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
        int sent = 0;
        while (sent < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_disposal.IsDisposed)
            {
                throw new IOException(
                    "The connection was closed part way through a write. See the note on partial writes below.");
            }

            int remaining = buffer.Length - sent;

            if (_reservedUploadBandwidth < remaining)
            {
                int needed = remaining - _reservedUploadBandwidth;
                int requestAmount = Math.Max(needed, ProtocolConstants.UploadBatchSize);

                int granted = await _bandwidthManager.RequestBandwidthAsync(
                    _user,
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

                _reservedUploadBandwidth += granted;
            }

            int canSend = Math.Min(remaining, _reservedUploadBandwidth);
            int toSend = Math.Min(canSend, ChunkSize);

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

            _reservedUploadBandwidth -= toSend;
            sent += toSend;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed())
        {
            if (_reservedDownloadBandwidth > 0)
            {
                _bandwidthManager.ReturnBandwidth(_reservedDownloadBandwidth, _downloadChannels);
                _reservedDownloadBandwidth = 0;
            }

            if (_reservedUploadBandwidth > 0)
            {
                _bandwidthManager.ReturnBandwidth(_reservedUploadBandwidth, _uploadChannels);
                _reservedUploadBandwidth = 0;
            }

            if (disposing && !_leaveInnerOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
