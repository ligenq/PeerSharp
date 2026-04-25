using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;

namespace PeerSharp.Internals.Utp;

internal enum UtpState
{
    None,
    SynSend,
    SynRecv,
    Connected,
    Closing,
    Closed
}

internal class UtpStream : Stream
{
    // Current delay samples (last 3 samples for smoothing)
    private const int CurDelaySize = 3;

    // Base delay history per libutp - 13 slots for 2 minutes of history
    private const int DelayBaseHistory = 13;

    private const int DelayBaseUpdateInterval = 10000;

    // SACK (Selective Acknowledgment) support for VPN packet reordering
    // Extension type 1 = SACK per BEP-29
    private const byte ExtensionSack = 1;

    private const int MaxCwndIncreaseBytesPerRtt = 3000;
    private const uint MaxRemoteWndSize = 4 * 1024 * 1024;

    // 4MB safety limit
    private const int MaxSynRetries = 2;

    private const int MaxUdpMtu = 1500;
    private const int MaxWindowDecay = 100;
    private const int MinUdpMtu = 576;
    private const int MtuSearchGranularity = 16;
    private const int TargetDelay = 100000;

    // MTU discovery (libutp-style probing)
    private const int UtpHeaderSize = 20;

    // 10 seconds per slot = ~2 minutes total
    private readonly uint[] _curDelayHist = new uint[CurDelaySize];

    private readonly uint[] _delayBaseHist = new uint[DelayBaseHistory];
    private readonly Lock _lock = new();
    private readonly ILogger<UtpStream> _logger = TorrentLoggerFactory.CreateLogger<UtpStream>();
    private readonly IUtpManager _manager;

    private readonly Pipe _pipe = new Pipe();

    // Bounded channel to prevent unbounded memory growth from pipe writes
    // Using a limit of 1000 items (each typically < 1.5KB) = ~1.5MB max
    private readonly Channel<PacketBuffer?> _pipeWriteChannel = Channel.CreateBounded<PacketBuffer?>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task? _pipeWriteTask;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private readonly PriorityQueue<ReceivedPacket, ushort> _reorderBuffer = new(new SeqComparer());
    private readonly HashSet<ushort> _reorderBufferSeqs = new();

    // SACK SUPPORT: Dictionary for O(1) lookup/removal on selective ACKs
    // Key = SeqNr, allows fast removal when SACK indicates receipt
    private readonly Dictionary<ushort, SentPacket> _sentPackets = new();

    private readonly Queue<ushort> _sentSeqQueue = new();
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(0);
    private ushort _ackNr;

    // Track seqs to prevent duplicates
    private TaskCompletionSource<bool>? _connectTcs;

    private int _curDelayIdx = 0;

    // Congestion Control (LEDBAT)
    private double _cwnd = 2600;

    private int _delayBaseIdx = 0;
    private DateTimeOffset _delayBaseTime = DateTimeOffset.MinValue;
    private AtomicDisposal _disposal = new();
    private int _duplicateAckCount;
    private bool _finReceived;
    private bool _finSent;
    private ushort _lastAckedSeq;
    private DateTimeOffset _lastCwndLog = DateTimeOffset.MinValue;

    // Window decay tracking
    private DateTimeOffset _lastDecayTime = DateTimeOffset.MinValue;

    private double _lastLoggedCwnd = 0;
    private DateTimeOffset _lastMaxedOutWindow = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReceiveTime;
    private uint _lastReplyDelay;
    private DateTimeOffset _lastSendTime;

    // 100ms target delay per LEDBAT
    private int _mss;

    private int _mtuCeiling;
    private DateTimeOffset _mtuDiscoverUntil;

    // Start with 2 MSS per libutp
    private int _mtuFloor;

    // libutp terminates after 2 SYN timeouts
    private int _mtuLast;

    private ushort _mtuProbeSeq;
    private int _mtuProbeSize;
    private DateTimeOffset _nextTimeout;
    private ushort _oldestUnackedSeq;
    private int _packetTimeout = 1000;

    // Track last cumulative ACK for duplicate detection
    private byte[]? _peerExtensionBits;

    private uint _rtt;
    private uint _rttVar;

    // Track oldest unacked for cumulative ACK processing
    private int _sentBytesUnacked;

    private ushort _seqNr;

    // Slow-start phase per libutp
    private bool _slowStart = true;

    private double _ssthresh = double.MaxValue;
    private UtpState _state = UtpState.None;

    // Slow-start threshold
    private int _timeoutCount;

    private uint _wndSize = 65535;

    public UtpStream(IUtpManager manager, IPEndPoint remote, ushort idRecv, ushort idSend, TimeProvider timeProvider)
    {
        _manager = manager;
        RemoteEndPoint = remote;
        ConnectionIdRecv = idRecv;
        ConnectionIdSend = idSend;
        _timeProvider = timeProvider;
        // Use Random.Shared for thread-safe random number generation (.NET 6+)
        // This avoids creating new Random() instances which can have poor entropy
        // when created in quick succession
        _seqNr = (ushort)Random.Shared.Next(0, 65535);
        _nextTimeout = _timeProvider.GetUtcNow().AddSeconds(3);
        _lastSendTime = _timeProvider.GetUtcNow();
        _lastReceiveTime = _timeProvider.GetUtcNow();
        ResetMtu();

        // Start the pipe write processing task
        _pipeWriteTask = ProcessPipeWriteChannelAsync();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public IPEndPoint RemoteEndPoint { get; }
    internal ushort ConnectionIdRecv { get; }
    internal ushort ConnectionIdSend { get; }

    public void CheckTimeout()
    {
        lock (_lock)
        {
            if (_state == UtpState.Closed || _disposal.IsDisposed)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();

            // INACTIVITY TIMEOUT: If we haven't received anything for 60s, close.
            if ((now - _lastReceiveTime).TotalSeconds > 60)
            {
                _logger.LogWarning("uTP {Remote}: Inactivity timeout - no packets received for 60s", RemoteEndPoint);
                CloseInternal(false, new TimeoutException("Inactivity timeout"));
                return;
            }

            // Window decay per libutp - decay by 0.5x every 100ms if no ACKs
            if (_sentPackets.Count > 0)
            {
                DecayWindow();
            }

            if (now > _nextTimeout)
            {
                if (_sentPackets.Count > 0)
                {
                    bool ignoreLoss = false;
                    if (_mtuProbeSeq != 0 && _sentPackets.Count == 1 && _oldestUnackedSeq == _mtuProbeSeq)
                    {
                        HandleMtuProbeLoss("PROBE-TIMEOUT");
                        ignoreLoss = true;
                    }

                    // Timeout congestion event per libutp: reset to single packet size
                    double oldCwnd = _cwnd;
                    if (!ignoreLoss)
                    {
                        _cwnd = _mss; // Reset to single MSS per libutp
                        _ssthresh = _cwnd; // Update slow-start threshold
                        _slowStart = false; // Exit slow-start on timeout
                        // Increase base timeout to prevent death spiral on high latency links where RTT samples are lost due to retransmits
                        _packetTimeout = Math.Min(_packetTimeout * 2, 10000);
                    }

                    _logger.LogDebug("LEDBAT {Remote}: TIMEOUT - cwnd {OldCwnd:F0}B -> {Cwnd:F0}B, timeoutCount={TimeoutCount}, mss={Mss}, packetTimeout={PacketTimeout}ms",
                        RemoteEndPoint, oldCwnd, _cwnd, _timeoutCount, _mss, _packetTimeout);

                    var resendList = new List<SentPacket>(4);
                    foreach (var seq in _sentSeqQueue)
                    {
                        if (resendList.Count >= 4)
                        {
                            break;
                        }
                        if (_sentPackets.TryGetValue(seq, out var pkt))
                        {
                            resendList.Add(pkt);
                        }
                    }

                    foreach (var pkt in resendList)
                    {
                        ResendPacket(pkt);
                    }
                    _timeoutCount++;
                    // Exponential backoff per libutp, cap at 30s
                    _nextTimeout = now.AddMilliseconds(Math.Min(30000, _packetTimeout * (1 << Math.Min(_timeoutCount, 4))));
                }
                else if (_state == UtpState.SynRecv)
                {
                    if (_timeoutCount >= MaxSynRetries)
                    {
                        _logger.LogWarning("uTP {Remote}: SYN-RECV timeout after {Count} retries", RemoteEndPoint, _timeoutCount);
                        CloseInternal(false, new TimeoutException("SYN-RECV timeout"));
                        return;
                    }

                    SendPacket(MessageType.ST_STATE, null);
                    _timeoutCount++;
                    _nextTimeout = now.AddMilliseconds(Math.Min(30000, _packetTimeout * (1 << _timeoutCount)));
                }
                else if (_state == UtpState.SynSend)
                {
                    if (_timeoutCount >= MaxSynRetries)
                    {
                        _logger.LogWarning("uTP {Remote}: SYN timeout after {Count} retries", RemoteEndPoint, _timeoutCount);
                        var ex = new TimeoutException($"Connection to {RemoteEndPoint} timed out after {MaxSynRetries} SYN retries");
                        _connectTcs?.TrySetException(ex);
                        CloseInternal(false, ex);
                        return;
                    }

                    // Resend existing SYN packet (don't create new one to avoid duplicate entries)
                    if (_sentPackets.TryGetValue(_oldestUnackedSeq, out var synPkt))
                    {
                        ResendPacket(synPkt);
                    }
                    _timeoutCount++;
                    _nextTimeout = now.AddMilliseconds(Math.Min(30000, _packetTimeout * (1 << _timeoutCount)));
                }
                else if (_state == UtpState.Closing && _sentPackets.Count == 0)
                {
                    CheckIfClosed();
                }
            }
            else if (_state == UtpState.Connected)
            {
                // KEEP-ALIVE and ZERO-WINDOW PROBING
                // Per libutp: KEEPALIVE_INTERVAL = 29000ms
                if ((now - _lastSendTime).TotalMilliseconds > 29000)
                {
                    _logger.LogDebug("uTP {Remote}: Sending keep-alive", RemoteEndPoint);
                    SendPacket(MessageType.ST_STATE, null);
                }
                // 2. Zero-window probing: If we have data to send but window is zero, probe every 5s
                else if (_wndSize == 0 && (now - _lastSendTime).TotalSeconds > 5)
                {
                    bool zeroWindowProbe = false;
                    try
                    {
                        if (_writeSemaphore.CurrentCount == 0)
                        {
                            zeroWindowProbe = true;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore if semaphore was disposed during shutdown.
                    }

                    if (zeroWindowProbe)
                    {
                        _logger.LogDebug("uTP {Remote}: Zero window probing", RemoteEndPoint);
                        SendPacket(MessageType.ST_STATE, null);
                    }
                }
            }
        }
    }

    public override void Close()
    {
        CloseInternal(true);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task connectTask;
        lock (_lock)
        {
            if (_state != UtpState.None)
            {
                throw new InvalidOperationException($"Cannot connect: stream is already in state {_state}");
            }

            _state = UtpState.SynSend;
            _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SendPacket(MessageType.ST_SYN, null);
            connectTask = _connectTcs.Task;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _connectTcs?.TrySetCanceled(cancellationToken);
            CloseInternal(false);
            throw new OperationCanceledException(cancellationToken);
        }

        // Register cancellation to abort the connection attempt
        await using var registration = cancellationToken.Register(() =>
        {
            _connectTcs?.TrySetCanceled(cancellationToken);
            CloseInternal(false);
        });

        await connectTask.ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            CloseInternal(true);

            // Wait for pipe write task to complete (with timeout to avoid hanging)
            if (_pipeWriteTask?.IsCompleted == false)
            {
#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
                try
                {
                    await _pipeWriteTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Task may have faulted, ignore during dispose
                }
#pragma warning restore RCS1075 // Avoid empty catch clause that catches System.Exception
            }

            _writeSemaphore.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override void Flush()
    { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous Read is not supported. Use ReadAsync instead.");
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var seq = result.Buffer;
            if (seq.IsEmpty && result.IsCompleted)
            {
                return 0;
            }

            var len = (int)Math.Min(seq.Length, buffer.Length);
            var slice = seq.Slice(0, len);
            slice.CopyTo(buffer.Span);
            _pipe.Reader.AdvanceTo(slice.End);
            return len;
        }
        catch (OperationCanceledException)
        {
            // If the stream is in a closed/closing state treat cancellation as EOF
            lock (_lock)
            {
                if (_state == UtpState.Closed || _finReceived)
                {
                    return 0;
                }
            }
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous Write is not supported. Use WriteAsync instead.");
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // Validate stream state before writing
        lock (_lock)
        {
            if (_state != UtpState.Connected)
            {
                throw new InvalidOperationException($"Cannot write: stream is in state {_state}, expected Connected");
            }
        }

        int sent = 0;
        while (sent < count)
        {
            // Wait for window space - check under lock to avoid race conditions
            bool canSend;
            lock (_lock)
            {
                // Re-check state in case connection was closed while waiting
                if (_state != UtpState.Connected)
                {
                    throw new IOException("Connection closed while writing");
                }
                double effectiveWindow = Math.Min(_wndSize, _cwnd);
                canSend = _sentBytesUnacked < effectiveWindow;
                if (!canSend)
                {
                    _lastMaxedOutWindow = _timeProvider.GetUtcNow();
                }
            }

            while (!canSend)
            {
                await _writeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    double effectiveWindow = Math.Min(_wndSize, _cwnd);
                    canSend = _sentBytesUnacked < effectiveWindow;
                    if (!canSend)
                    {
                        _lastMaxedOutWindow = _timeProvider.GetUtcNow();
                    }
                }
            }

            lock (_lock)
            {
                int chunk = Math.Min(count - sent, GetPayloadMss(0));
                SendPacket(MessageType.ST_DATA, buffer.AsMemory(offset + sent, chunk));
                sent += chunk;
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Validate stream state before writing
        lock (_lock)
        {
            if (_state != UtpState.Connected)
            {
                throw new InvalidOperationException($"Cannot write: stream is in state {_state}, expected Connected");
            }
        }

        int sent = 0;
        while (sent < buffer.Length)
        {
            // Wait for window space - check under lock to avoid race conditions
            bool canSend;
            lock (_lock)
            {
                // Re-check state in case connection was closed while waiting
                if (_state != UtpState.Connected)
                {
                    throw new IOException("Connection closed while writing");
                }
                double effectiveWindow = Math.Min(_wndSize, _cwnd);
                canSend = _sentBytesUnacked < effectiveWindow;
                if (!canSend)
                {
                    _lastMaxedOutWindow = _timeProvider.GetUtcNow();
                }
            }

            while (!canSend)
            {
                await _writeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    double effectiveWindow = Math.Min(_wndSize, _cwnd);
                    canSend = _sentBytesUnacked < effectiveWindow;
                    if (!canSend)
                    {
                        _lastMaxedOutWindow = _timeProvider.GetUtcNow();
                    }
                }
            }

            lock (_lock)
            {
                int chunk = Math.Min(buffer.Length - sent, GetPayloadMss(0));
                SendPacket(MessageType.ST_DATA, buffer.Slice(sent, chunk));
                sent += chunk;
            }
        }
    }

    /// <summary>
    /// Process packet with optional parsed SACK ranges.
    /// </summary>
    internal void ProcessPacketWithSack(
        MessageHeader header,
        byte[] data,
        int headerSize,
        List<(ushort Start, ushort End)>? sackRanges,
        byte[]? extensionBits,
        IPEndPoint remote)
    {
        lock (_lock)
        {
            // SECURITY: Verify packet source matches established connection
            if (!remote.Equals(RemoteEndPoint))
            {
                _logger.LogWarning("uTP {Remote}: Rejected packet from mismatching source {Source}", RemoteEndPoint, remote);
                return;
            }

            _lastReceiveTime = _timeProvider.GetUtcNow();
            UpdatePeerExtensionBits(extensionBits);

            if ((header.Type != MessageType.ST_SYN || _state != UtpState.None) && !IsAckNrValid(header.AckNr))
            {
                _logger.LogDebug("uTP {Remote}: Invalid ack_nr {AckNr} for seq {SeqNr}", RemoteEndPoint, header.AckNr, _seqNr);
                return;
            }
            // Update timestamps and RTT
            uint now = Utils.TimestampMicro();
            _lastReplyDelay = now - header.TimestampMicroseconds;
            // Protect against window inflation attacks by clamping to 4MB
            _wndSize = Math.Min(header.WndSize, MaxRemoteWndSize);

            // Handle ACKs with SACK support
            HandleAckWithSack(header.AckNr, header.TimestampDifferenceMicroseconds, sackRanges);

            switch (header.Type)
            {
                case MessageType.ST_STATE:
                    if (_state == UtpState.SynSend)
                    {
                        _state = UtpState.Connected;
                        _ackNr = (ushort)(header.SeqNr - 1); // Initialize AckNr
                        _connectTcs?.TrySetResult(true);
                        _logger.LogDebug("Connected to {Remote}", RemoteEndPoint);
                    }
                    else if (_state == UtpState.SynRecv)
                    {
                        _state = UtpState.Connected;
                        _logger.LogDebug("Accepted connection from {Remote}", RemoteEndPoint);
                    }
                    break;

                case MessageType.ST_DATA:
                case MessageType.ST_FIN:
                    if (_state == UtpState.SynRecv)
                    {
                        _state = UtpState.Connected;
                    }
                    HandleData(header, data, headerSize);
                    break;

                case MessageType.ST_SYN:
                    if (_state == UtpState.None)
                    {
                        _state = UtpState.SynRecv;
                        _ackNr = header.SeqNr;
                        SendPacket(MessageType.ST_STATE, null);
                    }
                    else if ((_state == UtpState.SynRecv || _state == UtpState.Connected) && header.SeqNr == _ackNr)
                    {
                        // Duplicate SYN (retransmission), resend ACK
                        _logger.LogDebug("uTP {Remote}: Received duplicate SYN, resending ACK", RemoteEndPoint);
                        SendPacket(MessageType.ST_STATE, null);
                    }
                    break;

                case MessageType.ST_RESET:
                    _logger.LogDebug("Reset from {Remote}", RemoteEndPoint);
                    // CRITICAL FIX: Fail pending connection with exception instead of cancellation
                    var ex = new IOException("Connection reset by remote peer");
                    _connectTcs?.TrySetException(ex);
                    CloseInternal(false, ex);
                    break;
            }

            // If connected, try to flush any pending data
            if (_state == UtpState.Connected)
            {
                FlushPendingWrites();
            }

            // Update timeout timer
            _nextTimeout = _timeProvider.GetUtcNow().AddMilliseconds(_packetTimeout);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            CloseInternal(true);
            _writeSemaphore.Dispose();
        }
        base.Dispose(disposing);
    }

    private static bool AreSameExtensions(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private void AdvanceSentQueue()
    {
        while (_sentSeqQueue.Count > 0 && !_sentPackets.ContainsKey(_sentSeqQueue.Peek()))
        {
            _sentSeqQueue.Dequeue();
        }
        _oldestUnackedSeq = _sentSeqQueue.Count > 0 ? _sentSeqQueue.Peek() : _seqNr;
    }

    private uint CalculateAvailableWindow()
    {
        // Our buffer is the _pipeWriteChannel (1000 items)
        // Estimate bytes based on remaining slots and current MSS
        int remainingSlots = 1000 - _pipeWriteChannel.Reader.Count;
        if (remainingSlots < 0)
        {
            remainingSlots = 0;
        }

        return (uint)(remainingSlots * GetPayloadMss(0));
    }

    private void CheckIfClosed()
    {
        // Symmetric close: we are closed only if we both sent and received FIN,
        // and our FIN has been acknowledged (no more sent packets).
        if (_finSent && _finReceived && _sentPackets.Count == 0 && _state != UtpState.Closed)
        {
            _state = UtpState.Closed;
            _logger.LogDebug("uTP {Remote}: Connection fully closed", RemoteEndPoint);
            _manager.CloseStream(this);
            ReleaseAllSentPackets();
            ReleaseReorderBuffer();
            _pipeWriteChannel.Writer.TryComplete();
        }
    }

    private void CheckReorderBuffer()
    {
        while (_reorderBuffer.Count > 0)
        {
            // Don't process more data after FIN
            if (_finReceived)
            {
                break;
            }

            var next = _reorderBuffer.Peek();

            if (next.SeqNr != (ushort)(_ackNr + 1))
            {
                break;
            }

            // Write payload if any
            if (next.Data.Length > 0 && !_pipeWriteChannel.Writer.TryWrite(next.Data))
            {
                break; // backpressure
            }

            _ackNr = next.SeqNr;
            _reorderBuffer.Dequeue();
            _reorderBufferSeqs.Remove(next.SeqNr);

            // FIN handling - after processing FIN, break to prevent further data processing
            if (next.IsFin && !_finReceived)
            {
                _finReceived = true;
                _logger.LogDebug("uTP {Remote}: Received FIN (out-of-order)", RemoteEndPoint);

                _pipeWriteChannel.Writer.TryWrite(null);

                if (_state == UtpState.Connected)
                {
                    _state = UtpState.Closing;
                }

                CheckIfClosed();
                break; // No more data after FIN
            }
        }
    }

    private void CloseInternal(bool sendFin, Exception? error = null)
    {
        lock (_lock)
        {
            if (_state == UtpState.Closed)
            {
                return;
            }

            if (sendFin)
            {
                if (!_finSent)
                {
                    _finSent = true;
                    _state = UtpState.Closing;
                    _logger.LogDebug("uTP {Remote}: Sending FIN", RemoteEndPoint);
                    SendPacket(MessageType.ST_FIN, null);
                }
            }
            else
            {
                // Force close
                _state = UtpState.Closed;
                _manager.CloseStream(this);
                ReleaseAllSentPackets();
                ReleaseReorderBuffer();
                _pipeWriteChannel.Writer.TryComplete(error);
            }

            CheckIfClosed();
            _connectTcs?.TrySetCanceled();
        }
    }

    /// <summary>
    /// Window decay per libutp - decay by 0.5x every 100ms if no ACKs received.
    /// Called from CheckTimeout.
    /// </summary>
    private void DecayWindow()
    {
        var now = _timeProvider.GetUtcNow();
        if ((now - _lastDecayTime).TotalMilliseconds >= MaxWindowDecay)
        {
            double oldCwnd = _cwnd;
            _cwnd = Math.Max(_cwnd * 0.5, _mss * 2);
            _lastDecayTime = now;
            if (Math.Abs(_cwnd - oldCwnd) > 0.001)
            {
                _logger.LogDebug("LEDBAT {Remote}: Window decay {OldCwnd:F0}B -> {Cwnd:F0}B", RemoteEndPoint, oldCwnd, _cwnd);
            }
        }
    }

    private void FlushPendingWrites()
    {
        if (_disposal.IsDisposed)
        {
            return;
        }

        double effectiveWindow = Math.Min(_wndSize, _cwnd);
        bool shouldRelease = false;

        try
        {
            if (_sentBytesUnacked < effectiveWindow && _writeSemaphore.CurrentCount == 0)
            {
                shouldRelease = true;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (shouldRelease)
        {
            try
            {
                _writeSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore race on disposal
            }
        }
    }

    private int GetPayloadMss(int extensionLen)
    {
        int mtu = _mtuLast > 0 ? _mtuLast : _mtuCeiling;
        int payload = mtu - UtpHeaderSize - extensionLen;
        return Math.Max(1, payload);
    }

    /// <summary>
    /// SACK SUPPORT: Handle ACK with optional SACK extension data.
    /// SACK allows the sender to know which out-of-order packets arrived,
    /// preventing false packet loss detection on VPNs with reordering.
    /// </summary>
    private void HandleAckWithSack(ushort ackNr, uint delay, List<(ushort Start, ushort End)>? sackRanges)
    {
        int ackedCount = 0;
        int ackedBytes = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // SACK SUPPORT: Remove selectively ACK'd packets immediately (libutp behavior)
        int sackMarked = 0;
        int rttSamples = 0;
        const int MaxRttSamplesPerAck = 3;
        ushort lastSent = (ushort)(_seqNr - 1);

        if (sackRanges != null)
        {
            foreach (var (start, end) in sackRanges)
            {
                // Sanity check: range must be in order and within outstanding window
                if (Utils.CompareSeq(start, end) > 0)
                {
                    continue;
                }

                for (ushort seq = start; Utils.CompareSeq(seq, end) <= 0; seq++)
                {
                    // Safety: don't process beyond what we actually sent
                    if (Utils.CompareSeq(seq, lastSent) > 0)
                    {
                        break;
                    }

                    if (_sentPackets.Remove(seq, out var pkt))
                    {
                        sackMarked++;

                        // Sample RTT on first SACK if not resent
                        if (!pkt.Resent && !pkt.RttSampled && rttSamples < MaxRttSamplesPerAck)
                        {
                            double packetRttMs = (now - pkt.SendTime).TotalMilliseconds;
                            if (packetRttMs > 0 && packetRttMs < 60000)
                            {
                                UpdateRtt((int)packetRttMs);
                                pkt.RttSampled = true;
                                rttSamples++;
                            }
                        }
                        ReleaseSentPacket(pkt);
                        ackedCount++;
                        ackedBytes += pkt.Length;
                        HandleMtuProbeAck(seq, pkt.Length);
                        _logger.LogDebug("SACK {Remote}: Selectively acked packet {SeqNr}", RemoteEndPoint, seq);
                    }

                    // Safety: if we somehow hit 65k iterations, stop (should be caught by CompareSeq)
                    if (seq == end)
                    {
                        break;
                    }
                }
            }
        }

        // Remove cumulatively acked packets (all packets up to and including ackNr)
        // Cap ackNr to lastSent to prevent walking into the future
        ushort effectiveAckNr = Utils.CompareSeq(ackNr, lastSent) > 0 ? lastSent : ackNr;

        // Only iterate if it's moving forward from oldest unacked
        int maxIterations = Math.Min(_sentPackets.Count + 1, 65536);
        int iterations = 0;

        if (Utils.CompareSeq(effectiveAckNr, _oldestUnackedSeq) >= 0)
        {
            for (ushort seq = _oldestUnackedSeq; Utils.CompareSeq(seq, effectiveAckNr) <= 0 && iterations < maxIterations; seq++)
            {
                iterations++;

                if (_sentPackets.Remove(seq, out var pkt))
                {
                    // Sample RTT if not already sampled by SACK and not resent
                    if (!pkt.Resent && !pkt.RttSampled && rttSamples < MaxRttSamplesPerAck)
                    {
                        double packetRttMs = (now - pkt.SendTime).TotalMilliseconds;
                        if (packetRttMs > 0 && packetRttMs < 60000)
                        {
                            UpdateRtt((int)packetRttMs);
                            rttSamples++;
                        }
                    }

                    ReleaseSentPacket(pkt);
                    ackedCount++;
                    ackedBytes += pkt.Length;
                    HandleMtuProbeAck(seq, pkt.Length);
                }

                // Safety: break if we wrapped around back to effectiveAckNr
                if (seq == effectiveAckNr)
                {
                    break;
                }
            }
        }

        // Update oldest unacked sequence
        if (ackedCount > 0 || sackMarked > 0)
        {
            AdvanceSentQueue();
        }

        if (ackedCount > 0 || sackMarked > 0)
        {
            _duplicateAckCount = 0;
            _timeoutCount = 0; // Reset timeout count on successful ACK
            if (ackedBytes > 0)
            {
                UpdateCongestionControl(ackedBytes, delay);
            }
            FlushPendingWrites();
            _lastAckedSeq = ackNr;
        }
        else if (_sentPackets.Count > 0 && ackNr == _lastAckedSeq)
        {
            // Duplicate ACK - but check if the missing packet was SACK'd
            _duplicateAckCount++;

            // SACK SUPPORT: Find the first un-SACK'd packet after ackNr
            ushort expectedSeq = (ushort)(ackNr + 1);
            bool missingPacketSackd = !_sentPackets.ContainsKey(expectedSeq);

            // Per libutp: DUPLICATE_ACKS_BEFORE_RESEND = 3
            if (_duplicateAckCount >= 3 && !missingPacketSackd)
            {
                if (_mtuProbeSeq != 0 && ackNr == (ushort)(_mtuProbeSeq - 1))
                {
                    HandleMtuProbeLoss("DUPACK");
                }

                var resendList = new List<SentPacket>(4);
                foreach (var seq in _sentSeqQueue)
                {
                    if (resendList.Count >= 4)
                    {
                        break;
                    }
                    if (_sentPackets.TryGetValue(seq, out var pkt) && !pkt.Resent)
                    {
                        resendList.Add(pkt);
                    }
                }

                if (resendList.Count > 0)
                {
                    double oldCwnd = _cwnd;
                    // Per libutp: halve window on packet loss
                    _cwnd = Math.Max(_cwnd * 0.5, _mss * 2);
                    _ssthresh = _cwnd; // Update slow-start threshold
                    _slowStart = false;

                    foreach (var pkt in resendList)
                    {
                        _logger.LogDebug("LEDBAT {Remote}: PACKET LOSS (3 dup ACKs) - cwnd CUT {OldCwnd:F0}B -> {Cwnd:F0}B, mss={Mss}, resending {SeqNr}",
                            RemoteEndPoint, oldCwnd, _cwnd, _mss, pkt.SeqNr);
                        ResendPacket(pkt);
                    }
                }
                _duplicateAckCount = 0;
            }
            else if (missingPacketSackd)
            {
                _logger.LogDebug("SACK {Remote}: Ignoring dup ACK - packet {SeqNr} already acked (reordered, not lost)", RemoteEndPoint, expectedSeq);
            }
        }

        CheckIfClosed();
    }

    private void HandleData(MessageHeader header, byte[] data, int headerSize)
    {
        ushort seq = header.SeqNr;

        if (Utils.CompareSeq(seq, _ackNr) <= 0)
        {
            // Already received
            SendPacket(MessageType.ST_STATE, null);
            return;
        }

        if (seq == (ushort)(_ackNr + 1))
        {
            // In order
            int payloadLen = data.Length - headerSize;
            bool isFin = header.Type == MessageType.ST_FIN;

            // Backpressure: check if we have space in the local channel
            // We need 1 slot for data and potentially 1 for FIN (null)
            int slotsNeeded = (payloadLen > 0 ? 1 : 0) + (isFin && !_finReceived ? 1 : 0);
            if (slotsNeeded > 0 && _pipeWriteChannel.Reader.Count + slotsNeeded > 1000)
            {
                _logger.LogWarning("uTP {Remote}: Local buffer full, backpressuring packet {SeqNr}", RemoteEndPoint, seq);
                // Send STATE to update window size but don't advance _ackNr
                SendPacket(MessageType.ST_STATE, null);
                return;
            }

            if (payloadLen > 0)
            {
                var payload = _pool.Rent(payloadLen);
                Array.Copy(data, headerSize, payload, 0, payloadLen);
                if (!_pipeWriteChannel.Writer.TryWrite(new PacketBuffer(payload, payloadLen, pooled: true)))
                {
                    _pool.Return(payload);
                }
            }
            _ackNr = seq;

            if (isFin && !_finReceived)
            {
                _finReceived = true;
                _logger.LogDebug("uTP {Remote}: Received FIN", RemoteEndPoint);
                _pipeWriteChannel.Writer.TryWrite(null);
                if (_state == UtpState.Connected)
                {
                    _state = UtpState.Closing;
                }
            }

            // Check reorder buffer
            CheckReorderBuffer();

            SendPacket(MessageType.ST_STATE, null);
            CheckIfClosed();
        }
        else
        {
            // Out of order - check for duplicates and buffer limit
            if (_reorderBuffer.Count < 1024 && !_reorderBufferSeqs.Contains(seq))
            {
                int payloadLen = data.Length - headerSize;
                var payload = _pool.Rent(payloadLen);
                Array.Copy(data, headerSize, payload, 0, payloadLen);

                _reorderBuffer.Enqueue(
                    new ReceivedPacket
                    {
                        SeqNr = seq,
                        Data = new PacketBuffer(payload, payloadLen, pooled: true),
                        IsFin = header.Type == MessageType.ST_FIN
                    },
                    seq);
                _reorderBufferSeqs.Add(seq);
            }
            SendPacket(MessageType.ST_STATE, null);
        }
    }

    private void HandleMtuProbeAck(ushort seq, int packetLength)
    {
        if (_mtuProbeSeq != 0 && seq == _mtuProbeSeq)
        {
            _mtuFloor = packetLength;
            MtuSearchUpdate();
            _logger.LogDebug("uTP {Remote}: MTU [ACK] floor={Floor} ceiling={Ceiling} current={Current}",
                RemoteEndPoint, _mtuFloor, _mtuCeiling, _mtuLast);
        }
    }

    private void HandleMtuProbeLoss(string reason)
    {
        if (_mtuProbeSeq == 0)
        {
            return;
        }

        _mtuCeiling = Math.Max(_mtuProbeSize - 1, _mtuFloor);
        MtuSearchUpdate();
        _logger.LogDebug("uTP {Remote}: MTU [{Reason}] floor={Floor} ceiling={Ceiling} current={Current}",
            RemoteEndPoint, reason, _mtuFloor, _mtuCeiling, _mtuLast);
    }

    private bool IsAckNrValid(ushort ackNr)
    {
        if (_sentPackets.Count == 0)
        {
            return true;
        }

        // Mirror libutp's ACK validation window to reject spoofed packets.
        ushort lastSent = (ushort)(_seqNr - 1);
        int allowedWindow = Math.Max(_sentPackets.Count + 3, 3);
        ushort oldestAllowed = (ushort)(lastSent - allowedWindow);

        if (Utils.CompareSeq(lastSent, ackNr) < 0)
        {
            return false;
        }

        if (Utils.CompareSeq(ackNr, oldestAllowed) < 0)
        {
            return false;
        }

        return true;
    }

    private void MaybeMarkMtuProbe(ushort seq, int packetLength)
    {
        if (_mtuProbeSeq != 0)
        {
            return;
        }

        if (_mtuFloor >= _mtuCeiling)
        {
            return;
        }

        if (_timeProvider.GetUtcNow() > _mtuDiscoverUntil)
        {
            ResetMtu();
        }

        if (packetLength > _mtuFloor && packetLength <= _mtuCeiling)
        {
            _mtuProbeSeq = seq;
            _mtuProbeSize = packetLength;
            _logger.LogDebug("uTP {Remote}: MTU probe seq={SeqNr} size={Size} floor={Floor} ceiling={Ceiling}",
                RemoteEndPoint, seq, packetLength, _mtuFloor, _mtuCeiling);
        }
    }

    private void MtuSearchUpdate()
    {
        if (_mtuFloor > _mtuCeiling)
        {
            _mtuFloor = _mtuCeiling;
        }

        _mtuLast = (_mtuFloor + _mtuCeiling) / 2;
        _mtuProbeSeq = 0;
        _mtuProbeSize = 0;

        if (_mtuCeiling - _mtuFloor <= MtuSearchGranularity)
        {
            _mtuLast = _mtuFloor;
            _mtuCeiling = _mtuFloor;
            _mtuDiscoverUntil = _timeProvider.GetUtcNow().AddMinutes(30);
        }

        UpdateMssFromMtu();
    }

    private async Task ProcessPipeWriteChannelAsync()
    {
        Exception? error = null;
        try
        {
            while (await _pipeWriteChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_pipeWriteChannel.Reader.TryRead(out var data))
                {
                    if (data == null)
                    {
                        // graceful EOF from remote
                        return;
                    }

                    bool writeFailed = false;
                    try
                    {
                        await _pipe.Writer.WriteAsync(data.Value.Buffer.AsMemory(0, data.Value.Length)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Pipe write error");
                        error = ex;
                        writeFailed = true;
                    }

                    if (data.Value.Pooled)
                    {
                        _pool.Return(data.Value.Buffer);
                    }

                    if (writeFailed)
                    {
                        return;
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
            // expected during shutdown
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // Drain remaining items to return them to pool to prevent leaks
            while (_pipeWriteChannel.Reader.TryRead(out var data))
            {
                if (data?.Pooled == true)
                {
                    _pool.Return(data.Value.Buffer);
                }
            }

            // ALWAYS complete the pipe, optionally with error
            await _pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private void ReleaseAllSentPackets()
    {
        foreach (var pkt in _sentPackets.Values)
        {
            if (pkt.Pooled)
            {
                _pool.Return(pkt.Buffer);
            }
        }
        _sentPackets.Clear();
        _sentSeqQueue.Clear();
        _sentBytesUnacked = 0;
    }

    private void ReleaseReorderBuffer()
    {
        while (_reorderBuffer.Count > 0)
        {
            var item = _reorderBuffer.Dequeue();
            if (item.Data.Pooled)
            {
                _pool.Return(item.Data.Buffer);
            }
        }
        _reorderBufferSeqs.Clear();
    }

    private void ReleaseSentPacket(SentPacket pkt)
    {
        _sentBytesUnacked -= pkt.Length;
        if (pkt.Pooled)
        {
            _pool.Return(pkt.Buffer);
        }
    }

    private void ResendPacket(SentPacket pkt)
    {
        pkt.Resent = true;
        pkt.SendTime = _timeProvider.GetUtcNow();

        // Update ACK NR and Timestamp in-place
        UtpManager.WriteUInt16BigEndian(pkt.Buffer, 18, _ackNr);
        UtpManager.WriteUInt32BigEndian(pkt.Buffer, 4, Utils.TimestampMicro());
        UtpManager.WriteUInt32BigEndian(pkt.Buffer, 8, _lastReplyDelay);

        _lastSendTime = _timeProvider.GetUtcNow();
        // Fire-and-forget UDP send; cancellation isn't meaningful for datagrams here.
        var task = _manager.SendAsync(pkt.Buffer.AsMemory(0, pkt.Length), RemoteEndPoint, CancellationToken.None);
        if (!task.IsCompletedSuccessfully)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogTrace(t.Exception, "ResendPacket failed for {Remote}", RemoteEndPoint);
                }
            }, TaskScheduler.Default);
        }
    }

    private void ResetMtu()
    {
        _mtuCeiling = MaxUdpMtu;
        _mtuFloor = MinUdpMtu;
        _mtuDiscoverUntil = _timeProvider.GetUtcNow().AddMinutes(30);
        MtuSearchUpdate();
        _mtuLast = _mtuCeiling;
        UpdateMssFromMtu();
    }

    private void SendPacket(MessageType type, ReadOnlyMemory<byte> payload = default)
    {
        // Don't send packets on closed connections (except FIN during closing)
        if (_state == UtpState.Closed)
        {
            return;
        }

        // SACK SUPPORT: Calculate SACK extension if needed
        int extensionLen = 0;
        int maxBit = -1;
        Span<byte> bitmask = stackalloc byte[32];

        if (type == MessageType.ST_STATE && _reorderBufferSeqs.Count > 0)
        {
            bitmask.Clear();
            foreach (var seq in _reorderBufferSeqs)
            {
                int offset = (seq - _ackNr - 2) & 0xFFFF; // Wrap-around safe
                if (offset < 256)
                {
                    int byteIndex = offset / 8;
                    int bitIndex = offset % 8;
                    bitmask[byteIndex] |= (byte)(1 << bitIndex);
                    if (byteIndex > maxBit)
                    {
                        maxBit = byteIndex;
                    }
                }
            }

            if (maxBit >= 0)
            {
                extensionLen = 2 + (maxBit / 8) + 1;
            }
        }

        int payloadLen = payload.Length;
        int maxPayload = GetPayloadMss(extensionLen);
        if (payloadLen > maxPayload)
        {
            _logger.LogWarning("uTP {Remote}: Payload {Payload} exceeds MTU-based MSS {Mss}", RemoteEndPoint, payloadLen, maxPayload);
            throw new InvalidOperationException("Payload exceeds current MTU limits");
        }

        int totalLen = 20 + extensionLen + payloadLen;
        // Always use pool for buffers now
        byte[] buffer = _pool.Rent(totalLen);

        // Header
        buffer[0] = (byte)(((byte)type << 4) | 1);
        // SACK SUPPORT: Set extension field if we have SACK data
        buffer[1] = extensionLen > 0 ? ExtensionSack : (byte)0;

        ushort connId = type == MessageType.ST_SYN ? ConnectionIdRecv : ConnectionIdSend;
        UtpManager.WriteUInt16BigEndian(buffer, 2, connId);

        UtpManager.WriteUInt32BigEndian(buffer, 4, Utils.TimestampMicro());
        UtpManager.WriteUInt32BigEndian(buffer, 8, _lastReplyDelay);
        UtpManager.WriteUInt32BigEndian(buffer, 12, CalculateAvailableWindow());
        UtpManager.WriteUInt16BigEndian(buffer, 16, _seqNr);
        UtpManager.WriteUInt16BigEndian(buffer, 18, _ackNr);

        // SACK SUPPORT: Add SACK extension after header
        if (extensionLen > 0)
        {
            buffer[20] = 0; // No more extensions
            buffer[21] = (byte)(extensionLen - 2);
            bitmask.Slice(0, extensionLen - 2).CopyTo(buffer.AsSpan(22));

            _logger.LogDebug("SACK {Remote}: Sending SACK extension with {Count} out-of-order packets, {Len} bytes",
                RemoteEndPoint, _reorderBufferSeqs.Count, extensionLen - 2);
        }

        if (payloadLen > 0)
        {
            payload.Span.CopyTo(buffer.AsSpan(20 + extensionLen));
        }

        bool isReliabilityPacket = type == MessageType.ST_DATA || type == MessageType.ST_SYN || type == MessageType.ST_FIN;

        // Store for reliability
        if (isReliabilityPacket)
        {
            MaybeMarkMtuProbe(_seqNr, totalLen);
            var pkt = new SentPacket
            {
                SeqNr = _seqNr,
                Buffer = buffer,
                Length = totalLen,
                SendTime = _timeProvider.GetUtcNow(),
                Resent = false,
                RttSampled = false,
                Pooled = true
            };
            // SACK SUPPORT: Use Dictionary instead of Queue
            _sentPackets[_seqNr] = pkt;
            if (_sentPackets.Count == 1)
            {
                _oldestUnackedSeq = _seqNr;
            }
            _sentSeqQueue.Enqueue(_seqNr);
            _sentBytesUnacked += totalLen;
            _seqNr++;

            _lastSendTime = _timeProvider.GetUtcNow();
            // Fire-and-forget UDP send; cancellation isn't meaningful for datagrams here.
            var task = _manager.SendAsync(buffer.AsMemory(0, totalLen), RemoteEndPoint, CancellationToken.None);
            if (!task.IsCompletedSuccessfully)
            {
                _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogTrace(t.Exception, "SendPacket failed for {Remote}", RemoteEndPoint);
                    }
                }, TaskScheduler.Default);
            }
        }
        else
        {
            _lastSendTime = _timeProvider.GetUtcNow();
            _ = SendAsyncAndReturn(buffer, totalLen);
        }
    }

    private async Task SendAsyncAndReturn(byte[] buffer, int length)
    {
        try
        {
            await _manager.SendAsync(buffer.AsMemory(0, length), RemoteEndPoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "SendPacket failed for {Remote}", RemoteEndPoint);
        }
        finally
        {
            _pool.Return(buffer);
        }
    }

    // Decay every 100ms if no ACKs
    private void UpdateCongestionControl(int ackedBytes, uint delay)
    {
        // LEDBAT Congestion Control per libutp

        var now = _timeProvider.GetUtcNow();

        // Update base delay history (one slot every 10 seconds, 13 slots = ~2 minutes)
        if ((now - _delayBaseTime).TotalMilliseconds >= DelayBaseUpdateInterval)
        {
            _delayBaseIdx = (_delayBaseIdx + 1) % DelayBaseHistory;
            _delayBaseHist[_delayBaseIdx] = delay;
            _delayBaseTime = now;
        }
        else if (delay < _delayBaseHist[_delayBaseIdx] || _delayBaseHist[_delayBaseIdx] == 0)
        {
            _delayBaseHist[_delayBaseIdx] = delay;
        }

        // Calculate base delay as minimum across history
        uint baseDelay = uint.MaxValue;
        for (int i = 0; i < DelayBaseHistory; i++)
        {
            if (_delayBaseHist[i] > 0 && _delayBaseHist[i] < baseDelay)
            {
                baseDelay = _delayBaseHist[i];
            }
        }
        if (baseDelay == uint.MaxValue)
        {
            baseDelay = delay;
        }

        // Update current delay samples
        _curDelayHist[_curDelayIdx] = delay;
        _curDelayIdx = (_curDelayIdx + 1) % CurDelaySize;

        // Calculate current delay as minimum of recent samples (filters jitter)
        uint curDelay = uint.MaxValue;
        for (int i = 0; i < CurDelaySize; i++)
        {
            if (_curDelayHist[i] > 0 && _curDelayHist[i] < curDelay)
            {
                curDelay = _curDelayHist[i];
            }
        }
        if (curDelay == uint.MaxValue)
        {
            curDelay = delay;
        }

        // Our delay = current delay - base delay (queuing delay only)
        long ourDelay = Math.Max(0, curDelay - (long)baseDelay);

        // Update last decay time on successful ACK
        _lastDecayTime = now;

        long offTarget = TargetDelay - ourDelay;
        long offTargetLimited = Math.Min(offTarget, TargetDelay);

        double windowFactor = Math.Min(ackedBytes, _cwnd) / Math.Max(_cwnd, ackedBytes);
        double delayFactor = (double)offTargetLimited / TargetDelay;
        double scaledGain = MaxCwndIncreaseBytesPerRtt * windowFactor * delayFactor;

        if (scaledGain > 0 && (now - _lastMaxedOutWindow).TotalMilliseconds > 1000)
        {
            // If we haven't been window-limited recently, don't grow the window.
            scaledGain = 0;
        }

        double ledbatCwnd = _cwnd + scaledGain;

        // Slow-start phase - exponential growth until ssthresh or delay > 90% target
        if (_slowStart)
        {
            double ssCwnd = _cwnd + (windowFactor * _mss);

            // Exit slow-start if we hit ssthresh or detect delay > 90% target (per libutp)
            if (ssCwnd > _ssthresh)
            {
                _slowStart = false;
            }
            else if (ourDelay > TargetDelay * 9 / 10)
            {
                _slowStart = false;
                _ssthresh = _cwnd;
                _logger.LogDebug("LEDBAT {Remote}: Exiting slow-start at cwnd={Cwnd:F0}B, delay={Delay}us", RemoteEndPoint, _cwnd, ourDelay);
            }
            else
            {
                _cwnd = Math.Max(ssCwnd, ledbatCwnd);
            }
        }
        else
        {
            _cwnd = ledbatCwnd;
        }

        // Enforce limits
        double minWindow = Math.Max(_mss, 10);
        if (_cwnd < minWindow)
        {
            _cwnd = minWindow;
        }

        if (_cwnd > MaxRemoteWndSize)
        {
            _cwnd = MaxRemoteWndSize;
        }

        double changeRatio = _lastLoggedCwnd > 0 ? Math.Abs(_cwnd - _lastLoggedCwnd) / _lastLoggedCwnd : 1;
        if (changeRatio > 0.25 || (now - _lastCwndLog).TotalSeconds >= 10)
        {
            _logger.LogDebug("LEDBAT {Remote}: cwnd={Cwnd:F0}B ({CwndKb:F1}KB), delay={Delay}us, baseDelay={BaseDelay}us, rtt={Rtt}ms, slowStart={SlowStart}",
                RemoteEndPoint, _cwnd, _cwnd / 1024, ourDelay, baseDelay, _rtt, _slowStart);
            _lastLoggedCwnd = _cwnd;
            _lastCwndLog = now;
        }
    }

    private void UpdateMssFromMtu()
    {
        int mtu = _mtuLast > 0 ? _mtuLast : _mtuCeiling;
        _mss = Math.Max(1, mtu - UtpHeaderSize);
    }

    private void UpdatePeerExtensionBits(byte[]? extensionBits)
    {
        if (extensionBits == null || extensionBits.Length != 8)
        {
            return;
        }

        if (_peerExtensionBits == null || !AreSameExtensions(_peerExtensionBits, extensionBits))
        {
            _peerExtensionBits = extensionBits;
            _logger.LogDebug("uTP {Remote}: Updated extension bits {Bits}",
                RemoteEndPoint, BitConverter.ToString(extensionBits));
        }
    }

    private void UpdateRtt(int rtt)
    {
        // RTT estimation per libutp following RFC 6298

        // Ignore obviously bad RTT samples (clock skew, etc.)
        if (rtt <= 0 || rtt > 60000)
        {
            return;
        }

        if (_rtt == 0)
        {
            // First RTT sample: initialize per RFC 6298
            _rtt = (uint)rtt;
            _rttVar = (uint)rtt / 2;
        }
        else
        {
            // Per libutp: SRTT = SRTT - SRTT/8 + sample/8
            // RTTVAR = RTTVAR + |SRTT - sample|/4
            int delta = (int)(_rtt - rtt);
            _rttVar = (uint)(_rttVar + ((Math.Abs(delta) - (int)_rttVar) / 4));
            _rtt = (uint)(_rtt - (_rtt / 8) + (rtt / 8));
        }

        // RTO = SRTT + max(G, 4*RTTVAR) per RFC 6298
        int rttVarComponent = Math.Max(100, (int)_rttVar * 4);
        _packetTimeout = (int)_rtt + rttVarComponent;

        // Per BEP-29: minimum RTO is 500ms
        _packetTimeout = Math.Max(500, _packetTimeout);

        // Cap at reasonable maximum
        _packetTimeout = Math.Min(_packetTimeout, 10000);
    }

    private readonly struct PacketBuffer
    {
        public PacketBuffer(byte[] buffer, int length, bool pooled)
        {
            Buffer = buffer;
            Length = length;
            Pooled = pooled;
        }

        public byte[] Buffer { get; }
        public int Length { get; }
        public bool Pooled { get; }
    }

    private sealed class ReceivedPacket
    {
        public PacketBuffer Data { get; init; }
        public bool IsFin { get; internal set; }
        public ushort SeqNr { get; init; }
    }

    private sealed class SentPacket
    {
        public required byte[] Buffer { get; set; }
        public int Length { get; set; }
        public bool Pooled { get; set; }
        public bool Resent { get; set; }

        // Track if RTT was already sampled for this packet to avoid double-sampling
        public bool RttSampled { get; set; }

        public DateTimeOffset SendTime { get; set; }
        public ushort SeqNr { get; set; }
    }

    [ExcludeFromCodeCoverage]
    private sealed class SeqComparer : IComparer<ushort>
    {
        public int Compare(ushort a, ushort b)
        {
            return Utils.CompareSeq(a, b);
        }
    }
}
