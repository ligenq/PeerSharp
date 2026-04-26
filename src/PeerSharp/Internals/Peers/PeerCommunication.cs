using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Utp;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

/*
 * THREAD-SAFETY GUIDELINES FOR THIS FILE:
 *
 * PeerCommunication represents a single peer connection with multiple async loops:
 * - ReceiveLoopAsync: Reads and processes incoming messages
 * - SendLoopAsync: Writes outgoing messages from queue
 * - WatchdogLoop: Monitors connection health
 *
 * Synchronization Strategy:
 *
 * 1. Interlocked: For state flags and counters
 *    - _connected: Connection state (0/1)
 *    - _peerChoking, _peerInterested, _amChoking, _amInterested: Protocol state
 *    - _uploaded, _downloaded: Transfer statistics
 *    - _largeMessageCount, _totalMessageCount: Rate limiting counters
 *
 * 2. MessageQueue: For send queue (_sendQueue)
 *    - Bounded channel for message ordering
 *    - Multiple producers (various Send* methods), single consumer (SendLoopAsync)
 *
 * 3. CancellationTokenSource: For coordinated shutdown (_cts)
 *    - Cancel triggers graceful shutdown of all loops
 *
 * KEY INVARIANTS:
 * - Only one thread reads from _stream (ReceiveLoopAsync)
 * - Only one thread writes to _stream (SendLoopAsync)
 * - Close() is idempotent (uses Interlocked.Exchange on _connected)
 * - All Send* methods are thread-safe (add to queue)
 */

internal class EncryptedStream : Stream
{
    private const int ChunkSize = ProtocolConstants.BlockSize;
    private readonly IBandwidthManager _bandwidthManager;
    private readonly string[] _downloadChannels;
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly ProtocolEncryption _pe;
    private readonly string[] _uploadChannels;
    private readonly IBandwidthUser _user;
    private AtomicDisposal _disposal = new();

    // CRITICAL FIX: Track reserved bandwidth for proper cleanup
    // These are returned to the BandwidthManager on disposal to prevent bandwidth leaks
    private int _reservedDownloadBandwidth;

    private int _reservedUploadBandwidth;

    public EncryptedStream(
        Stream inner,
        ProtocolEncryption pe,
        IBandwidthUser user,
        IBandwidthManager bandwidthManager,
        string[] downloadChannels,
        string[] uploadChannels,
        bool leaveInnerOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pe = pe;
        _user = user;
        _bandwidthManager = bandwidthManager;
        _downloadChannels = downloadChannels;
        _uploadChannels = uploadChannels;
        _leaveInnerOpen = leaveInnerOpen;
    }

    // CRITICAL FIX: Finalizer ensures bandwidth is returned even if Dispose is not called
    // This prevents permanent bandwidth leaks when exceptions cause objects to be GC'd
    ~EncryptedStream()
    {
        Dispose(false);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int r = _inner.Read(buffer, offset, count);
        if (r > 0)
        {
            _pe.Decrypt(buffer, offset, r);
        }

        return r;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int toRead = buffer.Length;
        if (toRead > ChunkSize)
        {
            toRead = ChunkSize;
        }

        if (_reservedDownloadBandwidth < toRead)
        {
            // Optimize: Request bandwidth in larger chunks (256KB) to reduce lock contention in BandwidthManager
            // But don't request absurdly large amounts if we only need a little
            int needed = toRead - _reservedDownloadBandwidth;
            int batchSize = ProtocolConstants.DownloadBatchSize;
            int requestAmount = Math.Max(needed, batchSize);

            int granted = await _bandwidthManager.RequestBandwidthAsync(
                _user,
                requestAmount,
                1,
                _downloadChannels,
                cancellationToken
            ).ConfigureAwait(false);

            if (granted <= 0)
            {
                return 0;
            }

            _reservedDownloadBandwidth += granted;
        }

        // Use from reservation
        int canRead = Math.Min(toRead, _reservedDownloadBandwidth);

        // Note: We don't catch OperationCanceledException here to return canRead because the reservation
        // is still tracked in _reservedDownloadBandwidth and will be returned on Dispose().
        int r = await _inner.ReadAsync(buffer.Slice(0, canRead), cancellationToken).ConfigureAwait(false);

        if (r > 0)
        {
            _reservedDownloadBandwidth -= r;
            _pe.Decrypt(buffer.Span.Slice(0, r));
        }

        return r;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
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
        var buf = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            Array.Copy(buffer, offset, buf, 0, count);
            _pe.Encrypt(buf, 0, count);
            _inner.Write(buf, 0, count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var encryptedBuf = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(encryptedBuf);
            _pe.Encrypt(encryptedBuf.AsSpan(0, buffer.Length));

            int sent = 0;
            while (sent < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int remaining = buffer.Length - sent;

                // If we have enough reserved, use it
                if (_reservedUploadBandwidth < remaining)
                {
                    // Reserve more, batching small requests
                    int needed = remaining - _reservedUploadBandwidth;
                    int batchSize = ProtocolConstants.UploadBatchSize;
                    int requestAmount = Math.Max(needed, batchSize);

                    int granted = await _bandwidthManager.RequestBandwidthAsync(
                        _user,
                        requestAmount,
                        1,
                        _uploadChannels,
                        cancellationToken
                    ).ConfigureAwait(false);

                    if (granted <= 0)
                    {
                        // If we can't get bandwidth, we can't send.
                        // Bandwidth was not granted, so nothing to return.
                        break;
                    }

                    _reservedUploadBandwidth += granted;
                }

                int canSend = Math.Min(remaining, _reservedUploadBandwidth);
                // Clamp to ChunkSize to ensure we don't block the underlying stream for too long
                int toSend = Math.Min(canSend, ChunkSize);

                // Note: We don't catch OperationCanceledException here to return toSend because
                // the reservation is still tracked and will be returned on Dispose().
                await _inner.WriteAsync(encryptedBuf.AsMemory(sent, toSend), cancellationToken).ConfigureAwait(false);

                _reservedUploadBandwidth -= toSend;
                sent += toSend;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedBuf);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed())
        {
            // CRITICAL FIX: Return any unused reserved bandwidth to prevent leaks
            // This runs both for explicit Dispose() and finalizer to ensure cleanup
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

internal class PeerCommunication : IPeerCommunication, IBandwidthUser, IAsyncDisposable
{
    // Default connection timeout in milliseconds (used if adaptive timeout not provided)
    private const int DefaultConnectionTimeoutMs = 10000;

    // Protocol State & Safety
    // SECURITY: Reduced from 6MB to 2MB. Typical piece data is 16KB-256KB.
    // 2MB allows for large bitfields and metadata while preventing DoS.
    private const int MaxLargeMessagesPerMinute = 100;

    // SECURITY: Rate limit for ALL messages to prevent small message floods
    // 5000/min = ~83/sec - allows high throughput but prevents extreme DoS
    private const int MaxMessagesPerMinute = 5000;

    private const int MinChokePeriodSeconds = 10;
    private const int SendQueueBaseSoftLimit = 150;
    private const int SendQueueCapacityMax = 1000;
    private const int SendQueueCapacityMin = 200;
    private const int SendQueueTimeoutMs = ProtocolConstants.SendQueueTimeoutMs;
    private readonly HashSet<int> _allowedFastPieces = new();
    private readonly Lock _fastPiecesLock = new();
    private readonly int _lastLoggedPipelineDepth = 0;
    private readonly ILogger<PeerCommunication> _logger = TorrentLoggerFactory.CreateLogger<PeerCommunication>();
    private readonly MessageQueue _sendQueue;
    private readonly List<int> _suggestedPieces = new();
    private readonly Torrent _torrent;
    private readonly TimeProvider _timeProvider;

    // Cached snapshots to avoid allocations on hot path reads
    private IReadOnlyList<int>? _allowedFastSnapshot;

    private int _amChoking = 1;
    private int _amInterested;
    private int _connected;

    // Track connection start time for adaptive timeout recording
    private long _connectionStartTicks;

    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private long _downloaded;
    private bool _encryptionHandshakeComplete;
    private bool _firstMessageProcessed = false;
    private Task? _handshakeLoopTask;
    private bool _handshakePreRead = false;
    private int _largeMessageCount = 0;
    private long _largeMessageWindowStart = Environment.TickCount64;

    // Rate limit for messages > 64KB
    private long _lastActivityTicksValue = Environment.TickCount64;

    private DateTimeOffset _lastChokeChange = DateTimeOffset.MinValue;
    private long _lastDownloaded;
    private DateTimeOffset _lastSendQueueLog = DateTimeOffset.MinValue;
    private long _lastUploaded;
    private int _messagesSentSinceLastLog = 0;
    private int _peerChoking = 1;
    private int _peerInterested;
    private byte[]? _plaintextBuffer;
    private byte[] _preReadHandshake = Array.Empty<byte>();
    private int _receiveLoopState = 0;

    // CRITICAL FIX: Track background tasks to eliminate fire-and-forget patterns
    // These tasks are awaited during Close() to ensure proper cleanup
    private Task? _receiveLoopTask;

    private Task? _sendLoopTask;

    // Smoothed speed uses exponential moving average to prevent feedback loops
    // where a peer becomes "slow" just because they finished their requests
    private int _smoothedDownloadSpeed;

    // RTT tracking for adaptive request pipelining
    private int _smoothedRttMs = 100;

    private int _strikes;
    private IReadOnlyList<int>? _suggestedSnapshot;
    private int _totalMessageCount = 0;
    private long _totalMessageWindowStart = Environment.TickCount64;
    private long _uploaded;

    public PeerCommunication(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
    {
        _torrent = torrent;
        Listener = listener;
        this._timeProvider = timeProvider;
        PeerPieces = new PiecesProgress(torrent.Pieces.Count);
        UtPex = new UtPex(this);
        UtMetadata = new UtMetadata(this);
        UtHolepunch = new UtHolepunch(this);

        // BEP 30: Initialize ut_hash_piece extension for Merkle hash torrents
        if (torrent.InfoFile.Info.IsMerkle)
        {
            UtHashPiece = new UtHashPiece(this, torrent);
        }

        // Use Wait mode to provide back-pressure. DropNewest causes protocol violations
        // (missing blocks/messages) which kills throughput.
        _sendQueue = new MessageQueue(SendQueueCapacityMax);
    }

    private enum EncryptionHandshakeResult
    { Success, Failed, PlaintextDetected, ConnectionClosed }

    public int AllowedFastCount
    {
        get
        {
            lock (_fastPiecesLock)
            {
                return _allowedFastPieces.Count;
            }
        }
    }

    public bool AmChoking => Volatile.Read(ref _amChoking) == 1;
    public bool AmInterested => Volatile.Read(ref _amInterested) == 1;
    public string Country { get; set; } = "";
    public long Downloaded => Interlocked.Read(ref _downloaded);
    public int DownloadSpeed { get; private set; }
    public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicksValue);
    public IPeerListener Listener { get; }
    public string Name => RemoteEndPoint?.ToString() ?? "Unknown";

    public bool PeerChoking
    {
        get => Volatile.Read(ref _peerChoking) == 1;
        private set => Volatile.Write(ref _peerChoking, value ? 1 : 0);
    }

    public byte[] PeerId { get; } = new byte[20];

    public bool PeerInterested
    {
        get => Volatile.Read(ref _peerInterested) == 1;
        private set => Volatile.Write(ref _peerInterested, value ? 1 : 0);
    }

    public PiecesProgress PeerPieces { get; private set; }

    /// <summary>
    /// BEP 40: Canonical peer priority. Higher values indicate more preferred peers.
    /// </summary>
    public uint Priority { get; set; }

    public System.Net.IPEndPoint? RemoteEndPoint { get; internal set; }

    public ExtensionHandshake? RemoteExtensions { get; private set; }

    public bool RemoteSupportsExtensions { get; private set; }

    /// <summary>
    /// BEP-6 Fast Extension support. Enables HaveAll, HaveNone, Suggest, AllowedFast, Reject messages.
    /// </summary>
    public bool RemoteSupportsFastExtension { get; private set; }

    /// <summary>
    /// BEP-52 BitTorrent v2 support. Indicates peer can handle v2 info hashes and Merkle trees.
    /// </summary>
    public bool RemoteSupportsV2 { get; private set; }

    public int SmoothedDownloadSpeed => Volatile.Read(ref _smoothedDownloadSpeed);

    public int SmoothedRttMs => Interlocked.CompareExchange(ref _smoothedRttMs, 0, 0);

    public int Strikes
    {
        get => _strikes;
        set => Interlocked.Exchange(ref _strikes, value);
    }

    public long Uploaded => Interlocked.Read(ref _uploaded);

    public int UploadSpeed { get; private set; }

    /// <summary>
    /// BEP 30: ut_hash_piece extension for Merkle hash torrents.
    /// </summary>
    public UtHashPiece? UtHashPiece { get; }

    IUtHashPiece? IPeerCommunication.UtHashPiece => UtHashPiece;

    public UtHolepunch UtHolepunch { get; }

    IUtHolepunch IPeerCommunication.UtHolepunch => UtHolepunch;

    public UtMetadata UtMetadata { get; }

    IUtMetadata IPeerCommunication.UtMetadata => UtMetadata;

    public virtual IUtPex UtPex { get; }

    IUtPex IPeerCommunication.UtPex => UtPex;

    internal TcpClient? Client { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint", "S2292:Trivial properties should be auto-implemented", Justification = "Backing field used with Interlocked")]
    internal int Connected { get => _connected; set => _connected = value; }

    internal Stream? Stream { get; set; }

    internal UtpStream? UtpStream { get; set; }

    // Connection-scoped token; falls back to non-cancelable when not connected yet.
    private CancellationToken ConnectionToken => _cts?.Token ?? CancellationToken.None;

    internal static void ConfigureTcpClient(TcpClient client, Settings settings, ILogger logger)
    {
        try
        {
            client.NoDelay = settings.Connection.TcpNoDelay;
        }
        catch (SocketException ex)
        {
            logger.LogTrace(ex, "Failed to set TcpNoDelay");
        }

        int recvBuffer = settings.Connection.TcpReceiveBufferBytes;
        if (recvBuffer > 0)
        {
            try
            {
                client.ReceiveBufferSize = recvBuffer;
            }
            catch (SocketException ex)
            {
                logger.LogTrace(ex, "Failed to set TcpReceiveBufferBytes={Size}", recvBuffer);
            }
        }

        int sendBuffer = settings.Connection.TcpSendBufferBytes;
        if (sendBuffer > 0)
        {
            try
            {
                client.SendBufferSize = sendBuffer;
            }
            catch (SocketException ex)
            {
                logger.LogTrace(ex, "Failed to set TcpSendBufferBytes={Size}", sendBuffer);
            }
        }
    }

    public void AddDownloaded(long bytes)
    {
        Interlocked.Add(ref _downloaded, bytes);
    }

    public void AddUploaded(long bytes)
    {
        Interlocked.Add(ref _uploaded, bytes);
    }

    public void AssignBandwidth(int amount)
    { }

    public void Choke()
    {
        var now = _timeProvider.GetUtcNow();
        if (_lastChokeChange != DateTimeOffset.MinValue &&
            (now - _lastChokeChange) < TimeSpan.FromSeconds(MinChokePeriodSeconds))
        {
            return;
        }

        if (Interlocked.Exchange(ref _amChoking, 1) == 1)
        {
            return;
        }

        _logger.LogDebug("CHOKING peer {PeerName} (speed={DownloadSpeed}B/s, interested={PeerInterested})", Name, DownloadSpeed, PeerInterested);
        _ = SendMessageAsync(new PeerMessage(MessageId.Choke));
        _lastChokeChange = now;
    }

    public async virtual Task CloseAsync()
    {
        bool wasConnected = Interlocked.Exchange(ref _connected, 0) == 1;

        if (wasConnected)
        {
            var stack = new StackTrace();
            _logger.LogDebug("Closing connection to {PeerName}. wasConnected=true. Trace: {Trace}", Name, stack.ToString());
        }

        await CleanupResourcesAsync().ConfigureAwait(false);

        // Only notify listener if we were actually connected
        if (wasConnected)
        {
            await Listener.ConnectionClosedAsync(this, 0).ConfigureAwait(false);
        }
    }

    // 10 seconds
    public async virtual Task<bool> ConnectAsync(string ip, int port, bool useUtp, CancellationToken ct = default)
    {
        return await ConnectAsync(ip, port, useUtp, DefaultConnectionTimeoutMs, ct).ConfigureAwait(false);
    }

    public async virtual Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, CancellationToken ct = default)
    {
        _logger.LogDebug("Connecting to {Ip}:{Port} (uTP: {UseUtp}, timeout: {Timeout}ms)", ip, port, useUtp, timeoutMs);

        // Record start time for adaptive timeout tracking
        _connectionStartTicks = Stopwatch.GetTimestamp();

        // CRITICAL: Initialize CTS BEFORE any async operations to prevent race conditions
        // where Close() might be called before CTS exists, or tasks might start with null token
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            // Use provided timeout (from adaptive timeout manager)
            using var connectTimeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ConnectionToken, connectTimeoutCts.Token, ct);

            if (useUtp && _torrent.UtpManager != null)
            {
                if (!CanUseUtpWithProxy(_torrent.Settings))
                {
                    _logger.LogWarning("uTP not supported through configured proxy, falling back to TCP");
                    useUtp = false;
                }
                else
                {
                    var ipAddress = System.Net.IPAddress.Parse(ip);
                    var endpoint = new System.Net.IPEndPoint(ipAddress, port);
                    UtpStream = _torrent.UtpManager.CreateStream(endpoint);
                    Stream = UtpStream;
                    RemoteEndPoint = endpoint;

                    _logger.LogDebug("Initiating uTP connection to {Endpoint}", endpoint);
                    await UtpStream.ConnectAsync(ct).WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    _logger.LogDebug("uTP connection to {Endpoint} successful", endpoint);
                }
            }

                if (!useUtp)
                {
                    var proxy = _torrent.Settings.Proxy;
                    if (proxy.Type != ProxyType.None && proxy.ProxyPeers && !string.IsNullOrEmpty(proxy.Host))
                    {
                    _logger.LogDebug("Connecting to {Ip}:{Port} via {ProxyType} proxy {ProxyHost}:{ProxyPort}", ip, port, proxy.Type, proxy.Host, proxy.Port);
                    var result = proxy.Type switch
                    {
                        ProxyType.Socks5 => await ProxyHelper.ConnectSocks5Async(ip, port, proxy.Host, proxy.Port, proxy.Username, proxy.Password, linkedCts.Token).ConfigureAwait(false),
                        ProxyType.Http => await ProxyHelper.ConnectHttpProxyAsync(ip, port, proxy.Host, proxy.Port, proxy.Username, proxy.Password, linkedCts.Token).ConfigureAwait(false),
                        _ => throw new NotSupportedException($"Proxy type {proxy.Type} not supported")
                    };

                        Stream = result.Stream;
                        Client = result.Client;
                        ConfigureTcpClient(Client, _torrent.Settings, _logger);

                    // Note: When connecting via proxy, the RemoteEndPoint of the TcpClient
                    // will be the proxy endpoint, not the peer endpoint.
                    // We set it manually here.
                    RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(ip), port);
                }
                    else
                    {
                        Client = new TcpClient();
                        ConfigureTcpClient(Client, _torrent.Settings, _logger);
                        await Client.ConnectAsync(ip, port, linkedCts.Token).ConfigureAwait(false);
                        Stream = Client.GetStream();
                        RemoteEndPoint = Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                    }
                }

            _connected = 1;
            _logger.LogDebug("Connected to {Ip}:{Port}", ip, port);

            var encryptionSetting = _torrent.Settings.Connection.Encryption;
            bool needsReconnect = false;

            // Try encryption handshake first (unless Encryption=Refuse)
            if (encryptionSetting != Encryption.Refuse)
            {
                var encryptionResult = await PerformEncryptionHandshakeAsync(true).ConfigureAwait(false);
                if (encryptionResult == EncryptionHandshakeResult.Success)
                {
                    // Read peer's BT handshake (they send it encrypted after Pe4)
                    if (!await ReadHandshakeAsync().ConfigureAwait(false))
                    {
                        _logger.LogDebug("Failed to read peer handshake after encryption success for {Ip}:{Port}", ip, port);
                        await CloseAsync().ConfigureAwait(false);
                        return false;
                    }

                    try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
                    StartBackgroundLoops();
                    return true;
                }
                else if (encryptionResult == EncryptionHandshakeResult.PlaintextDetected)
                {
                    // Peer sent plaintext response, already handled in handshake
                    _logger.LogDebug("Peer {Ip}:{Port} responded with plaintext, handshake complete", ip, port);
                    try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
                    StartBackgroundLoops();
                    return true;
                }
                else if (encryptionSetting == Encryption.Require)
                {
                    _logger.LogWarning("Encryption required but failed for {Ip}:{Port}", ip, port);
                    await CloseAsync().ConfigureAwait(false);
                    return false;
                }
                else if (encryptionResult == EncryptionHandshakeResult.ConnectionClosed)
                {
                    // Peer closed connection after receiving encryption handshake
                    // Need to reconnect with a fresh connection for plaintext
                    _logger.LogDebug("Peer {Ip}:{Port} closed connection (doesn't support encryption), reconnecting for plaintext", ip, port);
                    needsReconnect = true;
                }
                // Encryption failed and Allow mode - fall through to plaintext
            }

            // If peer closed the connection, we need a fresh connection for plaintext
            if (needsReconnect)
            {
                // CRITICAL FIX: Dispose old connection resources to prevent leaks
                // TcpClient.Close() is not sufficient - must call Dispose() to release all resources
                try { Client?.Dispose(); } catch { /* Ignore disposal errors during reconnect */ }
                try { UtpStream?.Close(); } catch { /* Ignore disposal errors during reconnect */ }
                Client = null;
                UtpStream = null;
                Stream = null;

                // Re-create CTS if it was cancelled during the failed attempt
                if (_cts?.IsCancellationRequested == true)
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();
                }

                // Small delay to avoid overwhelming the peer
                await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, ct).ConfigureAwait(false);

                // Reconnect
                try
                {
                    if (useUtp && _torrent.UtpManager != null)
                    {
                        var ipAddress = System.Net.IPAddress.Parse(ip);
                        var endpoint = new System.Net.IPEndPoint(ipAddress, port);
                        UtpStream = _torrent.UtpManager.CreateStream(endpoint);
                        Stream = UtpStream;
                        await UtpStream.ConnectAsync(ct).WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        Client = new TcpClient();
                        ConfigureTcpClient(Client, _torrent.Settings, _logger);
                        await Client.ConnectAsync(ip, port, linkedCts.Token).ConfigureAwait(false);
                        if (Client is not null)
                        {
                            Stream = Client.GetStream();
                        }
                    }
                    _logger.LogDebug("Reconnected to {Ip}:{Port} for plaintext", ip, port);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconnect failed for {Ip}:{Port}", ip, port);
                    await CloseAsync().ConfigureAwait(false);
                    return false;
                }
            }

            // Try plaintext handshake (encryption failed or Encryption=Refuse)
            _logger.LogDebug("Trying plaintext handshake with {Ip}:{Port}", ip, port);
            if (await PerformPlaintextHandshakeAsync().ConfigureAwait(false))
            {
                try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
                StartBackgroundLoops();
                return true;
            }
            else
            {
                _logger.LogDebug("Plaintext handshake failed for {Ip}:{Port}", ip, port);
                await CloseAsync().ConfigureAwait(false);
                return false;
            }
        }
        catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested)
        {
            // Connection timeout (not explicit cancellation via CloseAsync()) - expected in BitTorrent
            int elapsedMs = GetConnectionElapsedMs();
            _logger.LogDebug(ex, "Connect timeout {Ip}:{Port} - peer unresponsive after {Elapsed}ms", ip, port, elapsedMs);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            // OS-level connection timeout (timeoutMs > OS timeout)
            int elapsedMs = GetConnectionElapsedMs();
            _logger.LogDebug(ex, "Connect timeout {Ip}:{Port} - peer unresponsive after {Elapsed}ms", ip, port, elapsedMs);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            // Explicit cancellation via CloseAsync()
            _logger.LogDebug(ex, "Connect cancelled {Ip}:{Port}", ip, port);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex)
        {
            // Expected network errors - log without stack trace at Debug level
            _logger.LogDebug(ex, "Connect failed {Ip}:{Port} - {Message}", ip, port, ex.Message);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Expected network errors wrapped in IOException - log without stack trace
            _logger.LogDebug(ex, "Connect failed {Ip}:{Port} - {Message}", ip, port, ex.InnerException.Message);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected errors - log with stack trace
            _logger.LogError(ex, "Connect failed {Ip}:{Port} (unexpected)", ip, port);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await CleanupResourcesAsync().ConfigureAwait(false);

            // Wait for background tasks to complete with timeout
            // This ensures proper cleanup and prevents resource leaks
            var tasks = new List<Task>();
            if (_receiveLoopTask != null)
            {
                tasks.Add(_receiveLoopTask);
            }

            if (_sendLoopTask != null)
            {
                tasks.Add(_sendLoopTask);
            }

            if (_handshakeLoopTask != null)
            {
                tasks.Add(_handshakeLoopTask);
            }

            if (tasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (TimeoutException)
                {
                    // Expected if background tasks do not complete in time.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DisposeAsync task cleanup failed for {PeerName}", Name);
                }
            }

            _cts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Thread-safe cached snapshot of AllowedFast pieces for iteration.
    /// Returns cached snapshot, only allocates when collection changes.
    /// </summary>
    public IReadOnlyList<int> GetAllowedFastPieces()
    {
        lock (_fastPiecesLock)
        {
            return _allowedFastSnapshot ??= _allowedFastPieces.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets the elapsed time since connection attempt started in milliseconds.
    /// Used for adaptive timeout tracking.
    /// </summary>
    public int GetConnectionElapsedMs()
    {
        if (_connectionStartTicks == 0)
        {
            return 0;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - _connectionStartTicks;
        return (int)(elapsedTicks * 1000 / Stopwatch.Frequency);
    }

    /// <summary>
    /// <para>
    /// THROUGHPUT OPTIMIZATION: Calculate optimal request pipeline depth based on bandwidth-delay product.
    /// Pipeline = (Speed * RTT) / BlockSize, with min/max bounds.
    /// At startup, uses configured estimates to avoid slow ramp-up.
    /// </para>
    /// <para>
    /// SPEED STABILITY FIX: MaxPipeline reduced from 2000 to 250 to prevent:
    /// - Massive request backlogs that cause stalls when peers choke
    /// - Long recovery times after peer state changes
    /// - Memory pressure from thousands of pending requests
    /// 250 blocks = 4MB in-flight, sufficient for 1 Gbps with 30ms RTT
    /// </para>
    /// </summary>
    public int GetOptimalPipelineDepth()
    {
        const int BlockSize = 16 * 1024; // 16KB
        const int MinPipeline = 8;
        const int MaxPipeline = 128;

        var transferSettings = _torrent.Settings.Transfer;

        int speedBytesPerSec = Math.Max(DownloadSpeed, SmoothedDownloadSpeed);
        int rttMs = SmoothedRttMs;

        // THROUGHPUT OPTIMIZATION: Use estimated bandwidth/RTT at startup
        // Prevents slow ramp-up on high-speed connections (10x faster startup)
        if (speedBytesPerSec <= 0 || rttMs <= 0)
        {
            // Option 1: Use estimated bandwidth-delay product if configured
            if (transferSettings.EstimatedBandwidthBytesPerSec > 0 && transferSettings.EstimatedRttMs > 0)
            {
                long estimatedBytesInFlight = (long)transferSettings.EstimatedBandwidthBytesPerSec * transferSettings.EstimatedRttMs / 1000;
                long estimatedPipeline = estimatedBytesInFlight * 3 / 2 / BlockSize; // Add 50% headroom
                int initialPipeline = (int)Math.Clamp(estimatedPipeline, MinPipeline, MaxPipeline);

                _logger.LogDebug("Peer {RemoteEndPoint}: Using estimated pipeline depth {InitialPipeline} (BW={Bw}MB/s, RTT={Rtt}ms)",
                    RemoteEndPoint, initialPipeline, transferSettings.EstimatedBandwidthBytesPerSec / 1024 / 1024, transferSettings.EstimatedRttMs);
                return initialPipeline;
            }

            // Option 2: Use configured initial pipeline depth
            int configuredInitial = Math.Clamp(transferSettings.InitialPipelineDepth, MinPipeline, MaxPipeline);
            _logger.LogDebug("Peer {RemoteEndPoint}: Using configured initial pipeline depth {ConfiguredInitial}", RemoteEndPoint, configuredInitial);
            return configuredInitial;
        }

        // Normal operation: Use measured bandwidth-delay product
        // Bandwidth-delay product: bytes_in_flight = speed * rtt
        // Pipeline depth = bytes_in_flight / block_size
        // Add 50% headroom to keep the pipe full
        // All calculations in long to prevent overflow at high speeds
        long bytesInFlight = (long)speedBytesPerSec * rttMs / 1000;
        long pipelineLong = bytesInFlight * 3 / 2 / BlockSize;

        // Clamp in long space before casting to int to prevent overflow
        return (int)Math.Clamp(pipelineLong, MinPipeline, MaxPipeline);
    }

    public int GetAdaptivePipelineDepth()
    {
        int optimal = GetOptimalPipelineDepth();
        int strikes = Strikes;
        if (strikes > 0)
        {
            optimal = Math.Max(ProtocolConstants.MinPipelineDepth, optimal - (strikes * 10));
        }

        int rtt = SmoothedRttMs;
        if (rtt >= 800)
        {
            optimal = Math.Max(ProtocolConstants.MinPipelineDepth, optimal / 2);
        }

        return optimal;
    }

    /// <summary>
    /// Thread-safe cached snapshot of SuggestedPieces for iteration.
    /// Returns cached snapshot, only allocates when collection changes.
    /// </summary>
    public IReadOnlyList<int> GetSuggestedPieces()
    {
        lock (_fastPiecesLock)
        {
            return _suggestedSnapshot ??= _suggestedPieces.AsReadOnly();
        }
    }

    public void IncrementStrikes()
    {
        Interlocked.Increment(ref _strikes);
    }

    /// <summary>
    /// Thread-safe check if a piece is in the AllowedFast set.
    /// </summary>
    public bool IsAllowedFast(int pieceIndex)
    {
        lock (_fastPiecesLock)
        {
            return _allowedFastPieces.Contains(pieceIndex);
        }
    }

    // Default 100ms RTT estimate
    public void RecordRtt(int rttMs)
    {
        // Exponential moving average: new_rtt = 0.875 * old_rtt + 0.125 * sample
        // This smooths out jitter while still responding to changes
        int oldRtt = SmoothedRttMs;
        int newRtt = ((oldRtt * 7) + rttMs) / 8;
        Interlocked.Exchange(ref _smoothedRttMs, Math.Max(10, Math.Min(newRtt, 5000))); // Clamp 10ms-5s

        // Log significant RTT changes (>50% change)
        if (Math.Abs(newRtt - oldRtt) > oldRtt / 2)
        {
            int oldPipeline = _lastLoggedPipelineDepth > 0 ? _lastLoggedPipelineDepth : GetOptimalPipelineDepthForRtt(oldRtt);
            int newPipeline = GetOptimalPipelineDepthForRtt(newRtt);
            _logger.LogTrace("RTT significant change for {PeerName}: {OldRtt}ms -> {NewRtt}ms (sample={Sample}ms), pipeline depth {OldPipeline} -> {NewPipeline}",
                Name, oldRtt, newRtt, rttMs, oldPipeline, newPipeline);
        }
    }

    public async Task SendAllowedFastAsync(int pieceIndex)
    {
        // BEP-6: AllowedFast message is only valid if peer supports Fast Extension
        if (!RemoteSupportsFastExtension)
        {
            return;
        }

        var msg = new PeerMessage(MessageId.AllowedFast)
        {
            PieceIndex = pieceIndex
        };
        await SendMessageAsync(msg).ConfigureAwait(false);
    }

    /// <summary>
    /// Send HaveAll message (BEP-6). Only valid if peer supports Fast Extension.
    /// Returns true if message was sent, false if peer doesn't support it.
    /// </summary>
    public async Task<bool> SendHaveAllAsync()
    {
        // BEP-6: HaveAll is only valid if peer supports Fast Extension
        if (!RemoteSupportsFastExtension)
        {
            return false;
        }

        await SendMessageAsync(new PeerMessage(MessageId.HaveAll)).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SendHaveNoneAsync()
    {
        // BEP-6: HaveNone is only valid if peer supports Fast Extension
        if (!RemoteSupportsFastExtension)
        {
            return false;
        }

        await SendMessageAsync(new PeerMessage(MessageId.HaveNone)).ConfigureAwait(false);
        return true;
    }

    public async virtual Task SendMessageAsync(PeerMessage msg)
    {
        if (Interlocked.CompareExchange(ref _connected, 0, 0) == 0)
        {
            msg.PooledBlock?.Dispose();
            return;
        }

        if (ShouldDropNonCriticalMessage(msg))
        {
            msg.PooledBlock?.Dispose();
            return;
        }

        try
        {
            if (_sendQueue.TryEnqueue(msg))
            {
                return;
            }

            // Use WriteAsync with timeout to prevent indefinite blocking if send loop is stuck
            using var timeoutCts = new CancellationTokenSource(SendQueueTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ConnectionToken);
            await _sendQueue.EnqueueAsync(msg, linkedCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Expected when the send queue is closed during shutdown.
            msg.PooledBlock?.Dispose();
        }
        catch (OperationCanceledException ex)
        {
            if (_cts?.IsCancellationRequested == true)
            {
                msg.PooledBlock?.Dispose();
                throw;
            }

            // Timeout - send queue is backed up, likely network issue
            _logger.LogWarning(ex, "Send queue timeout for {PeerName} - queue backed up, closing connection", Name);
            msg.PooledBlock?.Dispose();
            await CloseAsync().ConfigureAwait(false);
        }
    }

    public Task SendHashRequestAsync(byte[] piecesRoot, int baseLayer, int index, int length, int proofLayers)
    {
        return SendMessageAsync(new PeerMessage(MessageId.HashRequest)
        {
            HashPiecesRoot = piecesRoot,
            HashBaseLayer = baseLayer,
            HashIndex = index,
            HashLength = length,
            HashProofLayers = proofLayers
        });
    }

    /// <summary>
    /// BEP 5: Send Port message to advertise our DHT UDP port to the peer.
    /// This allows the peer to add us to their DHT routing table.
    /// </summary>
    public async Task SendPortAsync(ushort dhtPort)
    {
        var msg = new PeerMessage(MessageId.Port)
        {
            Port = dhtPort
        };
        await SendMessageAsync(msg).ConfigureAwait(false);
    }

    public async Task SendRejectAsync(BlockRequest req)
    {
        // BEP-6: Reject message is only valid if peer supports Fast Extension
        if (!RemoteSupportsFastExtension)
        {
            return;
        }

        var msg = new PeerMessage(MessageId.Reject)
        {
            PieceIndex = req.PieceIndex,
            BlockOffset = req.Offset,
            BlockLength = req.Length
        };
        await SendMessageAsync(msg).ConfigureAwait(false);
    }

    public async Task<bool> SendRequestAsync(BlockRequest req)
    {
        if (_sendQueue.Count >= GetAdaptiveSendQueueLimit())
        {
            return false;
        }

        var msg = new PeerMessage(MessageId.Request)
        {
            PieceIndex = req.PieceIndex,
            BlockOffset = req.Offset,
            BlockLength = req.Length
        };
        return await TrySendMessageAsync(msg, timeoutMs: 250).ConfigureAwait(false);
    }

    public async Task SendSuggestAsync(int pieceIndex)
    {
        // BEP-6: Suggest message is only valid if peer supports Fast Extension
        if (!RemoteSupportsFastExtension)
        {
            return;
        }

        var msg = new PeerMessage(MessageId.Suggest)
        {
            PieceIndex = pieceIndex
        };
        await SendMessageAsync(msg).ConfigureAwait(false);
    }

    public async Task<bool> SetHandshakeReceivedAsync(byte[] handshake)
    {
        if (handshake.Length < 68)
        {
            return false;
        }

        // Validate info_hash matches our torrent (bytes 28-47)
        var receivedInfoHash = handshake.AsSpan(28, 20);
        bool hashMatches = false;

        // BEP 52: Check both v1 and v2 hashes for matching
        if (_torrent.InfoFile.Info.IsV1)
        {
            hashMatches = receivedInfoHash.SequenceEqual(_torrent.InfoFile.Info.Hash.Span);
        }

        if (!hashMatches && _torrent.InfoFile.Info.IsV2)
        {
            // For v2/hybrid torrents, also accept truncated v2 hash
            var truncatedV2Hash = _torrent.InfoFile.Info.HashV2.Span[..20];
            hashMatches = receivedInfoHash.SequenceEqual(truncatedV2Hash);
        }

        if (!hashMatches)
        {
            _logger.LogWarning("Info hash mismatch from {PeerName}", Name);
            return false;
        }

        _handshakePreRead = true;
        _preReadHandshake = handshake;

        // Extract reserved bytes flags (bytes 20-27)
        RemoteSupportsExtensions = (handshake[25] & 0x10) != 0; // BEP-10 extension protocol
        RemoteSupportsFastExtension = (handshake[27] & 0x04) != 0; // BEP-6 fast extension
        RemoteSupportsV2 = (handshake[27] & 0x10) != 0; // BEP-52 v2 protocol
        _logger.LogDebug("Peer {PeerName} capabilities: extensions={RemoteSupportsExtensions}, fast={RemoteSupportsFastExtension}, v2={RemoteSupportsV2}", Name, RemoteSupportsExtensions, RemoteSupportsFastExtension, RemoteSupportsV2);

        Array.Copy(handshake, 48, PeerId, 0, 20);
        PeerPieces = new PiecesProgress(_torrent.Pieces.Count);

        // Send extended handshake if peer supports it
        if (RemoteSupportsExtensions)
        {
            await SendExtendedHandshakeAsync().ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Set interested state and wait for message to be queued.
    /// </summary>
    public async Task SetInterestedAsync(bool interested)
    {
        int target = interested ? 1 : 0;
        if (Interlocked.Exchange(ref _amInterested, target) == target)
        {
            return;
        }

        await SendMessageAsync(new PeerMessage(interested ? MessageId.Interested : MessageId.NotInterested)).ConfigureAwait(false);
    }

    // Explicit interface implementation
    Task IPeerCommunication.SetInterestedAsync(bool interested)
    {
        return SetInterestedAsync(interested);
    }

    public void Start(Stream stream, ProtocolEncryption? encryption = null)
    {
        Stream = stream;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _connected = 1;
        _logger.LogDebug("Incoming connection from {PeerName}", Name);

        if (encryption != null)
        {
            Stream = new EncryptedStream(
                Stream,
                encryption,
                this,
                _torrent.Bandwidth,
                new[] { BandwidthManager.GlobalDownload, $"{_torrent.Hash.ToHexStringUpper()}_DL" },
                new[] { BandwidthManager.GlobalUpload, $"{_torrent.Hash.ToHexStringUpper()}_UL" },
                leaveInnerOpen: true
            );
            _encryptionHandshakeComplete = true;
        }

        _handshakeLoopTask = RunBackgroundTaskAsync(IncomingHandshakeLoopAsync, "IncomingHandshakeLoop", closeOnCompletion: false, ct: ConnectionToken);
    }

    public void StartAsInitiator(Stream stream)
    {
        Stream = stream;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _connected = 1;
        _logger.LogDebug("Connected stream peer {PeerName} starting as initiator", Name);
        _handshakeLoopTask = RunBackgroundTaskAsync(OutgoingConnectedHandshakeLoopAsync, "OutgoingConnectedHandshakeLoop", closeOnCompletion: false, ct: ConnectionToken);
    }

    public void Unchoke()
    {
        var now = _timeProvider.GetUtcNow();
        if (_lastChokeChange != DateTimeOffset.MinValue &&
            (now - _lastChokeChange) < TimeSpan.FromSeconds(MinChokePeriodSeconds))
        {
            return;
        }

        if (Interlocked.Exchange(ref _amChoking, 0) == 0)
        {
            return;
        }

        _logger.LogDebug("UNCHOKING peer {PeerName} (speed={DownloadSpeed}B/s, interested={PeerInterested})", Name, DownloadSpeed, PeerInterested);
        _ = SendMessageAsync(new PeerMessage(MessageId.Unchoke));
        _lastChokeChange = now;
    }

    public void UpdateSpeed()
    {
        long totalDown = Downloaded;
        long totalUp = Uploaded;

        DownloadSpeed = (int)(totalDown - _lastDownloaded);
        UploadSpeed = (int)(totalUp - _lastUploaded);

        _lastDownloaded = totalDown;
        _lastUploaded = totalUp;

        // SPEED STABILITY FIX: Use a more sophisticated smoothing algorithm inspired by libtransmission.
        // Instead of a simple 0.5/0.875 EMA, use a strategy that:
        // 1. Adopts higher speeds quickly (to find peaks)
        // 2. Adopts lower speeds SLOWLY (to ignore momentary stalls/jitter)
        // This prevents the "sawtooth" pattern where one bad second drops the average too much.

        int currentSmoothed, newSmoothed;
        do
        {
            currentSmoothed = SmoothedDownloadSpeed;
            if (DownloadSpeed > currentSmoothed)
            {
                // Quick Adoption (Peak Finding): new = 0.7 * old + 0.3 * sample
                // Faster than decay but slower than 0.5 to prevent jitter-sensitivity
                newSmoothed = ((currentSmoothed * 7) + (DownloadSpeed * 3)) / 10;
            }
            else
            {
                // Slow Decay (Hold Average): new = 0.95 * old + 0.05 * sample
                // Much slower decay (~13 seconds to half) to bridge network jitter
                newSmoothed = ((currentSmoothed * 19) + DownloadSpeed) / 20;
            }
        }
        while (Interlocked.CompareExchange(ref _smoothedDownloadSpeed, newSmoothed, currentSmoothed) != currentSmoothed);
    }

    internal static bool CanUseUtpWithProxy(Settings settings)
    {
        var proxy = settings.Proxy;
        if (proxy.Type == ProxyType.None || !proxy.ProxyPeers || string.IsNullOrEmpty(proxy.Host))
        {
            return true;
        }

        return proxy.Type == ProxyType.Socks5;
    }

    private void AddAllowedFastPiece(int pieceIndex)
    {
        lock (_fastPiecesLock)
        {
            _allowedFastPieces.Add(pieceIndex);
            _allowedFastSnapshot = null; // Invalidate cache
        }
    }

    private void AddSuggestedPiece(int pieceIndex)
    {
        lock (_fastPiecesLock)
        {
            _suggestedPieces.Add(pieceIndex);
            _suggestedSnapshot = null; // Invalidate cache
        }
    }

    private async Task CleanupResourcesAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        // Always dispose resources, even if we weren't fully connected
        // This prevents leaks when connection fails during ConnectAsync
        // Dispose the stream (handles EncryptedStream wrapper if present)
        // Note: EncryptedStream is created with leaveInnerOpen=true, so inner stream
        // is not double-closed when we close _client/_utpStream below
        try
        {
            if (Stream != null)
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            /* Ignore disposal errors */
        }

        try { Client?.Dispose(); } catch { /* Ignore disposal errors */ }
        try { UtpStream?.Close(); } catch { /* Ignore disposal errors */ }

        Client = null;
        UtpStream = null;
        Stream = null;

        _sendQueue.TryComplete();
    }

    private byte[] CreateHandshakeBuffer()
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        // Reserved bytes 20-27:
        handshake[25] |= 0x10; // Extension protocol bit (BEP-10)
        handshake[27] |= 0x04; // Fast Extension bit (BEP-6)

        // BEP 52: Set v2 support bit if this is a v2 or hybrid torrent
        if (_torrent.InfoFile.Info.IsV2)
        {
            handshake[27] |= 0x10; // V2 support bit (reserved[7] bit 4)
        }

        // BEP 52: Use appropriate hash for handshake
        // For hybrid torrents, use v1 hash for compatibility
        // For v2-only torrents, use truncated v2 hash (first 20 bytes)
        if (_torrent.InfoFile.Info.IsV1)
        {
            _torrent.InfoFile.Info.Hash.CopyTo(handshake, 28);
        }
        else if (_torrent.InfoFile.Info.IsV2)
        {
            // V2-only: use first 20 bytes of v2 hash
            _torrent.InfoFile.Info.HashV2.Span[..20].CopyTo(handshake.AsSpan(28));
        }

        _torrent.Settings.PeerId.CopyTo(handshake, 48);
        return handshake;
    }

    private int GetAdaptiveSendQueueLimit()
    {
        int speed = SmoothedDownloadSpeed;
        int extra = speed / 200_000 * 50;
        int limit = SendQueueBaseSoftLimit + extra;
        return Math.Clamp(limit, SendQueueCapacityMin, SendQueueCapacityMax);
    }

    private int GetOptimalPipelineDepthForRtt(int rttMs)
    {
        const int BlockSize = 16 * 1024;
        const int MinPipeline = 8;
        const int MaxPipeline = 128;
        int speedBytesPerSec = Math.Max(DownloadSpeed, SmoothedDownloadSpeed);
        if (speedBytesPerSec <= 0)
        {
            return MinPipeline;
        }

        // All calculations in long to prevent overflow at high speeds
        long bytesInFlight = (long)speedBytesPerSec * rttMs / 1000;
        long pipelineLong = bytesInFlight * 3 / 2 / BlockSize;
        // Clamp in long space before casting to int to prevent overflow
        return (int)Math.Clamp(pipelineLong, MinPipeline, MaxPipeline);
    }

    private async Task HandleExtendedMessageAsync(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        byte id = data[0];
        byte[] payload = data.Length > 1 ? data[1..] : Array.Empty<byte>();

        if (id == 0) // Handshake
        {
            if (BencodeParser.Parse(payload) is BDict dict)
            {
                RemoteExtensions = ExtensionHandshake.Parse(dict);
                UtMetadata.Init(RemoteExtensions);
                UtPex.Init(RemoteExtensions);
                UtHolepunch.Init(RemoteExtensions);

                // BEP 30: Initialize ut_hash_piece from remote handshake
                if (UtHashPiece != null && RemoteExtensions.MessageIds.TryGetValue(UtHashPiece.Name, out int hashPieceId))
                {
                    UtHashPiece.RemoteMessageId = (byte)hashPieceId;
                    _logger.LogDebug("BEP 30: Peer {PeerName} supports ut_hash_piece (ID={Id})", Name, hashPieceId);
                }

                _logger.LogDebug("{PeerName} supports extensions: {Extensions}", Name, string.Join(", ", RemoteExtensions.MessageIds.Keys));
                try { await Listener.ExtendedHandshakeFinishedAsync(this, RemoteExtensions).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "ExtendedHandshakeFinished callback error"); }
            }
        }
        else
        {
            if (UtMetadata.LocalMessageId.HasValue && UtMetadata.LocalMessageId.Value == id)
            {
                try { await Listener.ExtendedMessageReceivedAsync(this, id, payload).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "ExtendedMessageReceived callback error"); }
            }
            else if (UtPex.LocalMessageId.HasValue && UtPex.LocalMessageId.Value == id)
            {
                await UtPex.HandleMessageAsync(payload).ConfigureAwait(false);
            }
            else if (UtHolepunch.LocalMessageId.HasValue && UtHolepunch.LocalMessageId.Value == id)
            {
                await UtHolepunch.HandleMessageAsync(payload).ConfigureAwait(false);
            }
            else if (UtHashPiece?.LocalMessageId.HasValue == true && UtHashPiece.LocalMessageId.Value == id)
            {
                // BEP 30: Handle ut_hash_piece messages
                UtHashPiece.HandleMessage(payload);
            }
            else
            {
                try { await Listener.ExtendedMessageReceivedAsync(this, id, payload).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "ExtendedMessageReceived callback error"); }
            }
        }
    }

    private async Task IncomingHandshakeLoopAsync(CancellationToken token)
    {
        // If encryption was already established by PortListener/dispatcher, skip negotiation
        if (_encryptionHandshakeComplete)
        {
            await SendHandshakeAsync().ConfigureAwait(false);
            try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
            StartBackgroundLoops();
            return;
        }

        // Plaintext handshake already received and validated
        if (_handshakePreRead && _preReadHandshake.Length > 0 && _preReadHandshake[0] == 19)
        {
            // Send our handshake response for plaintext connections
            await SendHandshakeAsync().ConfigureAwait(false);
            try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
            StartBackgroundLoops();
            return;
        }

        var encryptionSetting = _torrent.Settings.Connection.Encryption;

        // Try encrypted handshake first (unless Encryption=Refuse)
        if (encryptionSetting != Encryption.Refuse)
        {
            var result = await PerformEncryptionHandshakeAsync(false).ConfigureAwait(false);
            if (result == EncryptionHandshakeResult.Success)
            {
                await SendHandshakeAsync().ConfigureAwait(false);
                try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
                StartBackgroundLoops();
                return;
            }
            else if (result == EncryptionHandshakeResult.PlaintextDetected)
            {
                // Handle as plaintext - fall through
                _logger.LogDebug("Incoming connection from {PeerName} is plaintext", Name);
            }
            else if (encryptionSetting == Encryption.Require)
            {
                _logger.LogWarning("Encryption required but incoming connection from {PeerName} failed encryption", Name);
                await CloseAsync().ConfigureAwait(false);
                return;
            }
            // Failed encryption in Allow mode - try plaintext
        }

        // Handle plaintext connection
        try
        {
            if (Stream == null) { await CloseAsync().ConfigureAwait(false); return; }

            byte[] hBuffer = new byte[68];
            int read = 0;

            // Use any buffered data from encryption attempt
            if (_plaintextBuffer?.Length > 0)
            {
                int toCopy = Math.Min(_plaintextBuffer.Length, 68);
                Array.Copy(_plaintextBuffer, 0, hBuffer, 0, toCopy);
                read = toCopy;
                _plaintextBuffer = null;
            }

            while (read < 68)
            {
                using var timeoutCts = new CancellationTokenSource(10000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                int r = await Stream.ReadAsync(hBuffer.AsMemory(read, 68 - read), linkedCts.Token).ConfigureAwait(false);
                if (r == 0) { await CloseAsync().ConfigureAwait(false); return; }
                Interlocked.Exchange(ref _lastActivityTicksValue, Environment.TickCount64);
                read += r;
            }

            if (hBuffer[0] != 19) { await CloseAsync().ConfigureAwait(false); return; }
            if (!hBuffer.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8)) { await CloseAsync().ConfigureAwait(false); return; }

            if (!await SetHandshakeReceivedAsync(hBuffer).ConfigureAwait(false)) { await CloseAsync().ConfigureAwait(false); return; }

            // Send our handshake response
            await SendHandshakeAsync().ConfigureAwait(false);

            try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
            StartBackgroundLoops();
        }
        catch (OperationCanceledException)
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Incoming handshake I/O error for {PeerName}", Name);
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Incoming handshake error for {PeerName}", Name);
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task OutgoingConnectedHandshakeLoopAsync(CancellationToken token)
    {
        try
        {
            if (!await PerformPlaintextHandshakeAsync().ConfigureAwait(false))
            {
                await CloseAsync().ConfigureAwait(false);
                return;
            }

            try { await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "HandshakeFinished callback error"); }
            StartBackgroundLoops();
        }
        catch (OperationCanceledException)
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Outgoing connected handshake I/O error for {PeerName}", Name);
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outgoing connected handshake error for {PeerName}", Name);
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<EncryptionHandshakeResult> PerformEncryptionHandshakeAsync(bool initiator)
    {
        try
        {
            // Capture Stream into a local so a concurrent CloseAsync/CleanupResourcesAsync
            // nulling the property doesn't cause a NullReferenceException mid-handshake.
            var stream = Stream;
            if (stream == null)
            {
                return EncryptionHandshakeResult.Failed;
            }

            var pe = new ProtocolEncryptionHandshake(_torrent.InfoFile.Info.Hash.ToArray(), initiator);

            if (initiator)
            {
                byte[] handshake = CreateHandshakeBuffer();
                pe.InitialPayload = handshake;
                var msg = pe.Initiate();
                await stream.WriteAsync(msg).ConfigureAwait(false);
            }

            byte[] buffer = new byte[4096];
            bool firstRead = true;
            while (!pe.IsComplete && !pe.IsError)
            {
                int read;
                try
                {
                    // Use a timeout for the first read to detect unresponsive peers quickly
                    using var timeoutCts = new CancellationTokenSource(firstRead ? 5000 : 30000);
                    read = await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogDebug(ex, "Encryption handshake timeout for {PeerName}", Name);
                    return EncryptionHandshakeResult.Failed;
                }

                if (read == 0)
                {
                    // Peer closed connection - likely doesn't support encryption
                    // Return ConnectionClosed so caller can reconnect with plaintext
                    return EncryptionHandshakeResult.ConnectionClosed;
                }

                Interlocked.Exchange(ref _lastActivityTicksValue, Environment.TickCount64);

                // Detect if peer responded with plaintext BitTorrent handshake instead of encryption
                if (firstRead && initiator && read >= 20 && buffer[0] == 19 && buffer.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8))
                {
                    _logger.LogDebug("Peer {PeerName} responded with plaintext instead of encryption", Name);
                    // Buffer the received data and handle as plaintext
                    _plaintextBuffer = buffer.AsSpan(0, read).ToArray();
                    return EncryptionHandshakeResult.PlaintextDetected;
                }
                firstRead = false;

                var data = buffer.AsSpan(0, read).ToArray();
                var resp = pe.HandleIncoming(data);

                if (resp.Length > 0)
                {
                    await stream.WriteAsync(resp).ConfigureAwait(false);
                }
            }

            if (pe.IsError)
            {
                return EncryptionHandshakeResult.Failed;
            }

            // CRITICAL FIX: Handle trailing data (e.g. BT Handshake) sent in the same packet as Pe4
            // Decrypt it and store in _plaintextBuffer so ReadHandshakeAsync can pick it up
            var trailing = pe.TrailingData;
            if (trailing.Length > 0 && pe.Encryption != null)
            {
                pe.Encryption.RC4In.Decrypt(trailing);
                _plaintextBuffer = trailing;
            }

            // Re-check the property: if CloseAsync ran while we were handshaking,
            // Stream will have been nulled and the underlying socket disposed,
            // so wrapping it in EncryptedStream would be pointless.
            if (Stream is not null)
            {
                Stream = new EncryptedStream(
                    stream,
                    pe.Encryption ?? new ProtocolEncryption(),
                    this,
                    _torrent.Bandwidth,
                    new[] { BandwidthManager.GlobalDownload, $"{_torrent.Hash.ToHexStringUpper()}_DL" },
                    new[] { BandwidthManager.GlobalUpload, $"{_torrent.Hash.ToHexStringUpper()}_UL" },
                    leaveInnerOpen: true // Inner stream is closed via _client or _utpStream in Close()
                    );

                if (pe.ReceivedPayload != null)
                {
                    await SetHandshakeReceivedAsync(pe.ReceivedPayload).ConfigureAwait(false);
                }

                return EncryptionHandshakeResult.Success;
            }
        }
        catch (SocketException ex)
        {
            // Expected network errors - log without stack trace
            _logger.LogDebug(ex, "Encryption handshake failed for {PeerName} - {Message}", Name, ex.Message);
            return EncryptionHandshakeResult.Failed;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Expected network errors wrapped in IOException
            _logger.LogDebug(ex, "Encryption handshake failed for {PeerName} - {Message}", Name, ex.InnerException.Message);
            return EncryptionHandshakeResult.Failed;
        }
        catch (Exception ex)
        {
            // Unexpected errors - log with stack trace
            _logger.LogError(ex, "Encryption handshake exception for {PeerName}", Name);
            return EncryptionHandshakeResult.Failed;
        }

        return EncryptionHandshakeResult.Failed;
    }

    private async Task<bool> PerformPlaintextHandshakeAsync()
    {
        if (Stream == null)
        {
            return false;
        }

        // Send our handshake
        await SendHandshakeAsync().ConfigureAwait(false);

        // Read and validate peer's handshake
        return await ReadHandshakeAsync().ConfigureAwait(false);
    }

    private async Task ProcessMessageAsync(PeerMessage? msg)
    {
        if (msg == null)
        {
            return;
        }

        if (msg.Id == MessageId.Bitfield && _firstMessageProcessed)
        {
            if (!_torrent.HasMetadata)
            {
                return;
            }
            throw new InvalidDataException("Bitfield must be the first message");
        }

        if (msg.Id != MessageId.Extended && msg.Id != MessageId.Port)
        {
            _firstMessageProcessed = true;
        }

        switch (msg.Id)
        {
            case MessageId.Choke:
                if (!PeerChoking)
                {
                    PeerChoking = true;
                    string mbps = (DownloadSpeed * 8 / 1_000_000.0).ToString("F2");
                    _logger.LogDebug("Peer {PeerName} CHOKED us (was downloading at {DownloadSpeed}B/s = {Mbps}Mbps)", Name, DownloadSpeed, mbps);
                }
                break;

            case MessageId.Unchoke:
                if (PeerChoking)
                {
                    PeerChoking = false;
                    _logger.LogDebug("Peer {PeerName} UNCHOKED us (current speed {DownloadSpeed}B/s) - requesting blocks", Name, DownloadSpeed);
                }
                break;

            case MessageId.Interested:
                if (!PeerInterested)
                {
                    PeerInterested = true;
                    _logger.LogDebug("Peer {PeerName} INTERESTED", Name);
                }
                break;

            case MessageId.NotInterested:
                if (PeerInterested)
                {
                    PeerInterested = false;
                    _logger.LogDebug("Peer {PeerName} NOT INTERESTED", Name);
                }
                break;

            case MessageId.Have:
                PeerPieces.AddPiece(msg.HavePieceIndex);
                break;

            case MessageId.Bitfield:
                PeerPieces.FromBitfield(msg.Data);
                _logger.LogDebug("{PeerName} sent bitfield: {Count} pieces", Name, PeerPieces.ReceivedCount);
                break;

            case MessageId.HaveAll:
                PeerPieces.SetHaveAll();
                _logger.LogDebug("{PeerName} has ALL pieces (FastExt)", Name);
                break;

            case MessageId.HaveNone:
                PeerPieces.SetHaveNone();
                _logger.LogDebug("{PeerName} has NO pieces (FastExt)", Name);
                break;

            case MessageId.Piece:
                // Piece data - pass to listener for processing
                // Block data is in msg.PooledBlock, handled by FileTransfer.BlockReceived
                await SafeNotifyListenerAsync(msg).ConfigureAwait(false);
                return; // Don't call MessageReceived again at end of method
            case MessageId.Cancel:
                // BEP-3: Peer is cancelling a previously requested block
                // Pass to listener so upload can remove the pending request
                _logger.LogDebug("{PeerName} CANCELLED request {PieceIndex}:{BlockOffset}", Name, msg.PieceIndex, msg.BlockOffset);
                await SafeNotifyListenerAsync(msg).ConfigureAwait(false);
                return; // Don't call MessageReceived again at end of method
            case MessageId.Suggest:
                AddSuggestedPiece(msg.PieceIndex);
                _logger.LogDebug("{PeerName} SUGGESTS piece {PieceIndex}", Name, msg.PieceIndex);
                break;

            case MessageId.AllowedFast:
                AddAllowedFastPiece(msg.PieceIndex);
                _logger.LogDebug("{PeerName} ALLOWED FAST piece {PieceIndex}", Name, msg.PieceIndex);
                break;

            case MessageId.Reject:
                _logger.LogDebug("{PeerName} REJECTED request {PieceIndex}:{BlockOffset}", Name, msg.PieceIndex, msg.BlockOffset);
                await SafeNotifyListenerAsync(msg).ConfigureAwait(false);
                break;

            case MessageId.Request:
                if (!AmChoking)
                {
                    await SafeNotifyListenerAsync(msg).ConfigureAwait(false);
                }
                break;

            case MessageId.HashRequest:
                await HandleHashRequestAsync(msg).ConfigureAwait(false);
                break;

            case MessageId.Hashes:
                if (msg.HashPiecesRoot != null)
                {
                    bool accepted = _torrent.InfoFile.Info.TryAddV2Hashes(
                        msg.HashPiecesRoot,
                        msg.HashBaseLayer,
                        msg.HashIndex,
                        msg.HashLength,
                        msg.HashProofLayers,
                        msg.Data);

                    _logger.LogDebug(
                        "{PeerName} sent BEP 52 hashes for root {PiecesRoot}; accepted={Accepted}",
                        Name,
                        Convert.ToHexString(msg.HashPiecesRoot),
                        accepted);
                }
                break;

            case MessageId.HashReject:
                _logger.LogDebug("{PeerName} rejected BEP 52 hash request for root {PiecesRoot}", Name, Convert.ToHexString(msg.HashPiecesRoot ?? Array.Empty<byte>()));
                break;

            case MessageId.Extended:
                await HandleExtendedMessageAsync(msg.Data).ConfigureAwait(false);
                break;

            case MessageId.Port:
                // BEP 5: Port message indicates peer's DHT UDP port
                _logger.LogDebug("{PeerName} advertised DHT port {Port}", Name, msg.Port);
                await SafeNotifyPortReceivedAsync(msg.Port).ConfigureAwait(false);
                break;
        }

        if (msg.Id != MessageId.Request && msg.Id != MessageId.Reject)
        {
            await SafeNotifyListenerAsync(msg).ConfigureAwait(false);
        }
    }

    private async Task HandleHashRequestAsync(PeerMessage msg)
    {
        if (!RemoteSupportsV2 || !_torrent.InfoFile.Info.IsV2 || msg.HashPiecesRoot == null)
        {
            await SendHashRejectAsync(msg).ConfigureAwait(false);
            return;
        }

        var hashes = _torrent.InfoFile.Info.GetV2Hashes(
            msg.HashPiecesRoot,
            msg.HashBaseLayer,
            msg.HashIndex,
            msg.HashLength,
            msg.HashProofLayers);

        if (hashes == null)
        {
            await SendHashRejectAsync(msg).ConfigureAwait(false);
            return;
        }

        await SendMessageAsync(new PeerMessage(MessageId.Hashes)
        {
            HashPiecesRoot = msg.HashPiecesRoot,
            HashBaseLayer = msg.HashBaseLayer,
            HashIndex = msg.HashIndex,
            HashLength = msg.HashLength,
            HashProofLayers = msg.HashProofLayers,
            Data = hashes
        }).ConfigureAwait(false);
    }

    private Task SendHashRejectAsync(PeerMessage request)
    {
        return SendMessageAsync(new PeerMessage(MessageId.HashReject)
        {
            HashPiecesRoot = request.HashPiecesRoot ?? new byte[32],
            HashBaseLayer = request.HashBaseLayer,
            HashIndex = request.HashIndex,
            HashLength = request.HashLength,
            HashProofLayers = request.HashProofLayers
        });
    }

    private async Task<bool> ReadHandshakeAsync()
    {
        try
        {
            if (Stream == null)
            {
                return false;
            }

            byte[] hBuffer = new byte[68];
            int read = 0;

            if (_plaintextBuffer?.Length > 0)
            {
                int toCopy = Math.Min(_plaintextBuffer.Length, 68);
                Array.Copy(_plaintextBuffer, 0, hBuffer, 0, toCopy);
                read = toCopy;

                if (_plaintextBuffer.Length > 68)
                {
                    _preReadHandshake = _plaintextBuffer.AsSpan(68).ToArray();
                }
                _plaintextBuffer = null;
            }

            while (read < 68)
            {
                using var timeoutCts = new CancellationTokenSource(10000);
                int r = await Stream.ReadAsync(hBuffer.AsMemory(read, 68 - read), timeoutCts.Token).ConfigureAwait(false);
                if (r == 0)
                {
                    return false;
                }

                Interlocked.Exchange(ref _lastActivityTicksValue, Environment.TickCount64);
                read += r;
            }

            if (hBuffer[0] != 19)
            {
                _logger.LogDebug("Invalid handshake from {PeerName}: wrong length byte {Length}", Name, hBuffer[0]);
                return false;
            }

            if (!hBuffer.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8))
            {
                _logger.LogDebug("Invalid handshake from {PeerName}: wrong protocol string", Name);
                return false;
            }

            return await SetHandshakeReceivedAsync(hBuffer).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Handshake timeout for {PeerName}", Name);
            return false;
        }
        catch (SocketException ex)
        {
            // Expected network errors
            _logger.LogDebug(ex, "Handshake read failed for {PeerName} - {Message}", Name, ex.Message);
            return false;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Expected network errors wrapped in IOException
            _logger.LogDebug(ex, "Handshake read failed for {PeerName} - {Message}", Name, ex.InnerException.Message);
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected errors
            _logger.LogError(ex, "Handshake read exception for {PeerName}", Name);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _receiveLoopState, 1, 0) != 0)
        {
            return;
        }

        if (Stream == null)
        {
            return;
        }

        var pipeReader = PipeReader.Create(Stream);
        bool handshakeReceived = _handshakePreRead;

        try
        {
            while (!token.IsCancellationRequested)
            {
                ReadResult result = await pipeReader.ReadAsync(token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastActivityTicksValue, Environment.TickCount64);
                var buffer = result.Buffer;

                if (!handshakeReceived)
                {
                    if (buffer.Length >= 68)
                    {
                        var hBuffer = buffer.Slice(0, 68).ToArray();
                        pipeReader.AdvanceTo(buffer.GetPosition(68));

                        if (hBuffer[0] != 19)
                        {
                            throw new InvalidDataException("Invalid handshake length");
                        }

                        if (!hBuffer.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8))
                        {
                            throw new InvalidDataException("Invalid protocol string");
                        }

                        // Use SetHandshakeReceived for consistent handling (validates info hash, extracts flags, sends extended handshake)
                        if (!await SetHandshakeReceivedAsync(hBuffer).ConfigureAwait(false))
                        {
                            throw new InvalidDataException("Info hash mismatch");
                        }
                        handshakeReceived = true;
                        await Listener.HandshakeFinishedAsync(this).ConfigureAwait(false);
                        continue;
                    }
                    else
                    {
                        pipeReader.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }
                }

                while (PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int consumed))
                {
                    long now = Environment.TickCount64;
                    bool isDataTransfer = message?.Id == MessageId.Piece || message?.Id == MessageId.Request;

                    // SECURITY: Rate limit ALL messages to prevent small message floods
                    // EXCEPTION: Piece/Request messages are expected to be frequent during transfer
                    if (!isDataTransfer)
                    {
                        long windowStart = Interlocked.Read(ref _totalMessageWindowStart);

                        // Reset counter every minute
                        if (now - windowStart > ProtocolConstants.RateLimitWindowMs)
                        {
                            Interlocked.Exchange(ref _totalMessageWindowStart, now);
                            Interlocked.Exchange(ref _totalMessageCount, 0);
                        }

                        int totalCount = Interlocked.Increment(ref _totalMessageCount);
                        if (totalCount >= MaxMessagesPerMinute)
                        {
                            throw new InvalidDataException($"SECURITY: Peer exceeded message rate limit ({totalCount} >= {MaxMessagesPerMinute}/min). Possible DoS attack.");
                        }
                    }

                    // SECURITY: Additional rate limit for large messages (> 64KB)
                    // EXCEPTION: Piece messages are large by design
                    int length = consumed - 4; // approximate payload length
                    if (length > 65536 && !isDataTransfer)
                    {
                        long windowStart = Interlocked.Read(ref _largeMessageWindowStart);

                        // Reset counter every minute
                        if (now - windowStart > ProtocolConstants.RateLimitWindowMs)
                        {
                            Interlocked.Exchange(ref _largeMessageWindowStart, now);
                            Interlocked.Exchange(ref _largeMessageCount, 0);
                        }

                        int count = Interlocked.Increment(ref _largeMessageCount);
                        if (count >= MaxLargeMessagesPerMinute)
                        {
                            throw new InvalidDataException($"SECURITY: Peer exceeded large message rate limit ({count} >= {MaxLargeMessagesPerMinute}/min). Possible DoS attack.");
                        }
                    }

                    await ProcessMessageAsync(message).ConfigureAwait(false);
                }

                pipeReader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _connected, 0, 0) == 1)
            {
                _logger.LogDebug(ex, "Receive error for {PeerName}", Name);
                await CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await pipeReader.CompleteAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _receiveLoopState, 0);
        }
    }

    /// <summary>
    /// CRITICAL FIX: Runs a background task with proper error handling and logging.
    /// This eliminates fire-and-forget patterns that silently swallow exceptions.
    /// </summary>
    private Task RunBackgroundTaskAsync(
        Func<CancellationToken, Task> taskFunc,
        string taskName,
        CancellationToken ct)
    {
        return RunBackgroundTaskAsync(taskFunc, taskName, closeOnCompletion: true, ct);
    }

    private async Task RunBackgroundTaskAsync(
        Func<CancellationToken, Task> taskFunc,
        string taskName,
        bool closeOnCompletion,
        CancellationToken ct)
    {
        bool faulted = false;
        try
        {
            await taskFunc(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Normal shutdown - expected
            _logger.LogDebug(ex, "{PeerName}: {TaskName} cancelled (normal shutdown)", Name, taskName);
            faulted = true;
        }
        catch (IOException ex)
        {
            // Connection closed - common, log at lower level
            _logger.LogDebug(ex, "{PeerName}: {TaskName} IO error", Name, taskName);
            faulted = true;
        }
        catch (Exception ex)
        {
            // Unexpected error - log with full details
            _logger.LogError(ex, "{PeerName}: {TaskName} failed", Name, taskName);
            faulted = true;
        }
        finally
        {
            if (closeOnCompletion || faulted)
            {
                // Ensure the connection is closed if a background task fails,
                // but allow handshake completion to keep the connection alive.
                await CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Safely invokes listener callback with exception isolation.
    /// Prevents listener errors from killing the peer connection.
    /// </summary>
    private async Task SafeNotifyListenerAsync(PeerMessage msg)
    {
        try
        {
            await Listener.MessageReceivedAsync(this, msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Message handler failed for {MessageId} from {PeerName}", msg.Id, Name);
        }
    }

    /// <summary>
    /// Safely invokes port received callback with exception isolation.
    /// </summary>
    private async Task SafeNotifyPortReceivedAsync(ushort port)
    {
        try
        {
            await Listener.PortReceivedAsync(this, port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Port handler failed for port {Port} from {PeerName}", port, Name);
        }
    }

    private async Task SendExtendedHandshakeAsync()
    {
        try
        {
            var handshake = new ExtensionHandshake
            {
                Client = "PeerSharp"
            };
            handshake.MessageIds[UtMetadata.Name] = 1;
            UtMetadata.SetLocalMessageId(1);

            // BEP 17: Don't advertise PEX support for private torrents
            if (!_torrent.InfoFile.Info.IsPrivate)
            {
                handshake.MessageIds[Extensions.UtPex.Name] = 2;
                UtPex.SetLocalMessageId(2);
            }

            handshake.MessageIds[UtHolepunch.Name] = 3;
            UtHolepunch.SetLocalMessageId(3);

            // BEP 30: Advertise ut_hash_piece support for Merkle hash torrents
            if (UtHashPiece != null)
            {
                handshake.MessageIds[UtHashPiece.Name] = 4;
                UtHashPiece.SetLocalMessageId(4);
            }

            if (_torrent.InfoFile.InfoBytes?.Length > 0)
            {
                handshake.MetadataSize = _torrent.InfoFile.InfoBytes.Length;
            }

            using var result = BencodeWriter.WriteToResult(handshake.ToBencode());

            var msg = new PeerMessage(MessageId.Extended)
            {
                Data = new byte[1 + result.Memory.Length]
            };
            msg.Data[0] = 0;
            result.Memory.Span.CopyTo(msg.Data.AsSpan(1));

            await SendMessageAsync(msg).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "SendExtendedHandshake error"); }
    }

    private async Task SendHandshakeAsync()
    {
        if (Stream == null)
        {
            return;
        }

        await Stream!.WriteAsync(CreateHandshakeBuffer()).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        if (Stream == null)
        {
            return;
        }

        try
        {
            while (await _sendQueue.WaitToReadAsync(token).ConfigureAwait(false))
            {
                int batchCount = 0;
                while (_sendQueue.TryDequeue(out var msg))
                {
                    var writeStart = _timeProvider.GetUtcNow();
                    await WriteMessageToStreamAsync(msg, token).ConfigureAwait(false);
                    var writeMs = (_timeProvider.GetUtcNow() - writeStart).TotalMilliseconds;

                    // Log slow writes that might indicate bandwidth throttling or network issues
                    if (writeMs > 100)
                    {
                        _logger.LogTrace("Slow send to {PeerName}: {MsgId} took {Elapsed}ms (possible bandwidth throttle or network issue)", Name, msg.Id, Math.Round(writeMs, 1));
                    }

                    msg.PooledBlock?.Dispose();
                    batchCount++;
                    _messagesSentSinceLastLog++;
                }

                // Log periodic send queue stats every 10 seconds
                var now = _timeProvider.GetUtcNow();
                if ((now - _lastSendQueueLog).TotalSeconds >= 10)
                {
                    var queueCount = _sendQueue.Count;
                    _logger.LogTrace("SendLoop {PeerName}: sent {Count} msgs in 10s, current queue depth={QueueDepth}", Name, _messagesSentSinceLastLog, queueCount);
                    _lastSendQueueLog = now;
                    _messagesSentSinceLastLog = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown - not an error
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Send error for {PeerName}", Name);
            await CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the background processing loops with proper task tracking.
    /// </summary>
    private void StartBackgroundLoops()
    {
        if (_cts == null)
        {
            return;
        }

        _receiveLoopTask = RunBackgroundTaskAsync(ReceiveLoopAsync, "ReceiveLoop", ConnectionToken);
        _sendLoopTask = RunBackgroundTaskAsync(SendLoopAsync, "SendLoop", ConnectionToken);
    }

    private async Task<bool> TrySendMessageAsync(PeerMessage msg, int timeoutMs)
    {
        if (Interlocked.CompareExchange(ref _connected, 0, 0) == 0)
        {
            msg.PooledBlock?.Dispose();
            return false;
        }

        if (ShouldDropNonCriticalMessage(msg))
        {
            msg.PooledBlock?.Dispose();
            return false;
        }

        try
        {
            if (_sendQueue.TryEnqueue(msg))
            {
                return true;
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ConnectionToken);
            await _sendQueue.EnqueueAsync(msg, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            // Expected when the send queue is closed during shutdown.
            msg.PooledBlock?.Dispose();
            return false;
        }
        catch (OperationCanceledException)
        {
            msg.PooledBlock?.Dispose();
            return false;
        }
    }

    private async Task WriteMessageToStreamAsync(PeerMessage msg, CancellationToken token)
    {
        if (Stream == null)
        {
            throw new InvalidOperationException("Cannot write message: stream is not connected");
        }

        if (msg.Id == MessageId.Interested)
        {
            _logger.LogTrace("Sending Interested to {PeerName}", Name);
        }

        if (msg.Id == MessageId.NotInterested)
        {
            _logger.LogTrace("Sending NotInterested to {PeerName}", Name);
        }

        int len = PeerProtocol.GetMessageLength(msg);

        // Use ArrayPool for packet construction
        byte[] packet = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            int written = PeerProtocol.WriteMessage(msg, packet.AsSpan(0, len));
            await Stream.WriteAsync(packet.AsMemory(0, written), token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private bool ShouldDropNonCriticalMessage(PeerMessage msg)
    {
        if (_sendQueue.Count < GetAdaptiveSendQueueLimit())
        {
            return false;
        }

        switch (msg.Id)
        {
            case MessageId.Have:
            case MessageId.Suggest:
            case MessageId.AllowedFast:
            case MessageId.Port:
                ThrottledQueueDropLog(msg.Id);
                return true;
            default:
                return false;
        }
    }

    private void ThrottledQueueDropLog(MessageId id)
    {
        var now = _timeProvider.GetUtcNow();
        if ((now - _lastSendQueueLog).TotalSeconds >= 10)
        {
            _lastSendQueueLog = now;
            _logger.LogDebug("Dropping non-critical {MessageId} for {PeerName} due to send queue backpressure (queue={QueueCount})",
                id, Name, _sendQueue.Count);
        }
    }
}

