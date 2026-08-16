using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Utp;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
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

/// <summary>
/// Encrypts and decrypts one peer connection.
///
/// <para>
/// Rate limiting deliberately does not live here. It used to, which meant a configured limit only
/// applied to connections that negotiated encryption and silently did nothing on plaintext ones. It is
/// now <see cref="RateLimitedStream"/>, layered underneath this one so it meters what the wire carries.
/// </para>
/// </summary>
internal class EncryptedStream : Stream
{
    private const int ChunkSize = ProtocolConstants.BlockSize;
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly ProtocolEncryption _pe;
    private AtomicDisposal _disposal = new();

    public EncryptedStream(
        Stream inner,
        ProtocolEncryption pe,
        bool leaveInnerOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pe = pe;
        _leaveInnerOpen = leaveInnerOpen;
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
        int toRead = Math.Min(buffer.Length, ChunkSize);

        int r = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        if (r > 0)
        {
            _pe.Decrypt(buffer.Span[..r]);
        }

        return r;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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

            // Handed down whole: the rate limited layer beneath splits it into chunks it has quota for.
            await _inner.WriteAsync(encryptedBuf.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
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
        if (_disposal.MarkDisposed() && disposing && !_leaveInnerOpen)
        {
            _inner.Dispose();
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
    private readonly HashSet<int> _allowedFastPieces = [];
    private readonly Lock _availabilityLock = new();
    private readonly Lock _fastPiecesLock = new();
    private readonly int _lastLoggedPipelineDepth = 0;
    private readonly ILogger<PeerCommunication> _logger;
    private IPeerListener _listener;
    private readonly MessageQueue _sendQueue;
    private readonly List<int> _suggestedPieces = [];
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

    /// <summary>When we last put anything on the wire, which is what drives keepalives.</summary>
    private long _lastSentTicksValue = Environment.TickCount64;

    private DateTimeOffset _lastChokeChange = DateTimeOffset.MinValue;
    private long _lastDownloaded;
    private DateTimeOffset _lastUnchokedAt = DateTimeOffset.MinValue;
    private long _uploadedAtLastUnchoke;
    private DateTimeOffset _lastSendQueueLog = DateTimeOffset.MinValue;
    private long _lastUploaded;
    private int _messagesSentSinceLastLog = 0;
    private int _peerChoking = 1;
    private int _peerInterested;
    private byte[]? _plaintextBuffer;
    private byte[] _preReadHandshake = [];
    private byte[]? _deferredBitfield;
    private readonly HashSet<int> _deferredHavePieces = [];
    private DeferredAvailabilityKind _deferredAvailabilityKind;

    /// <summary>
    /// Bytes that arrived after the handshake but were pulled off the socket along with it.
    ///
    /// <para>
    /// Peers routinely send their handshake and the messages that follow in one segment, so reading a
    /// fixed 68 bytes tends to take the start of the next message too. These are kept separate from
    /// <see cref="_preReadHandshake"/>, which holds the handshake itself: both used to live in that one
    /// field, so <see cref="SetHandshakeReceivedAsync"/> overwrote these moments after they were saved.
    /// The message stream then began mid-message, which is why the first decode on an otherwise healthy
    /// encrypted connection failed with a negative length.
    /// </para>
    /// </summary>
    private byte[] _bufferedAfterHandshake = [];
    private int _receiveLoopState = 0;

    // These tasks are awaited during Close() to ensure proper cleanup
    private Task? _receiveLoopTask;

    private Task? _sendLoopTask;

    // Smoothed speed uses exponential moving average to prevent feedback loops
    // where a peer becomes "slow" just because they finished their requests
    private long _smoothedDownloadSpeed;

    // RTT tracking for adaptive request pipelining
    private int _smoothedRttMs = 100;

    private int _strikes;
    private IReadOnlyList<int>? _suggestedSnapshot;
    private int _totalMessageCount = 0;
    private long _totalMessageWindowStart = Environment.TickCount64;
    private long _uploaded;
    private int _usefulDataExchanged;

    public PeerCommunication(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        : this(torrent, listener, timeProvider, NullLoggerFactory.Instance)
    {
    }

    internal PeerCommunication(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _torrent = torrent;
        _logger = loggerFactory.CreateLogger<PeerCommunication>();
        Volatile.Write(ref _listener, listener);
        this._timeProvider = timeProvider;
        PeerPieces = new PiecesProgress(torrent.Pieces.Count);
        UtPex = new UtPex(this);
        UtMetadata = new UtMetadata(this);
        UtHolepunch = new UtHolepunch(this);
        LtDontHave = new LtDontHave(this, loggerFactory.CreateLogger<LtDontHave>());

        // BEP 30: Initialize ut_hash_piece extension for Merkle hash torrents
        if (torrent.InfoFile.Info.IsMerkle)
        {
            UtHashPiece = new UtHashPiece(this, torrent, loggerFactory.CreateLogger<UtHashPiece>());
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
    public DateTimeOffset LastUnchokedAt => _lastUnchokedAt;
    public long UploadedSinceUnchoked => Uploaded - Interlocked.Read(ref _uploadedAtLastUnchoke);
    public string Country { get; set; } = "";
    public long Downloaded => Interlocked.Read(ref _downloaded);
    public long DownloadSpeed { get; private set; }
    public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicksValue);

    /// <summary>When we last sent anything. Distinct from receive activity: a peer drops us for being
    /// silent, regardless of how chatty it has been.</summary>
    public long LastSentTicks => Interlocked.Read(ref _lastSentTicksValue);
    public IPeerListener Listener => Volatile.Read(ref _listener);
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
    /// Whether the peer has told us which pieces it holds, by any of bitfield, have, have-all or
    /// have-none.
    ///
    /// <para>
    /// Without this, a peer that has said nothing is indistinguishable from one that holds nothing:
    /// both report zero pieces. The two call for opposite conclusions - a peer holding nothing wants
    /// everything we have, while a silent peer tells us nothing at all - and conflating them makes an
    /// ordinary swarm look full of peers we are failing to serve.
    /// </para>
    /// </summary>
    public bool HasReportedPieces { get; private set; }

    private enum DeferredAvailabilityKind
    {
        None,
        Bitfield,
        HaveAll,
        HaveNone
    }

    /// <summary>
    /// Moves an established connection to the peer manager created after magnet metadata arrives.
    /// The socket and its receive/send loops remain live; listener reads are ordinary reference reads,
    /// so replacing the target is atomic.
    /// </summary>
    internal void RetargetListener(IPeerListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listener = listener;
    }

    /// <summary>
    /// Resizes the peer bitfield once a magnet learns its piece count and replays availability that
    /// arrived while the count was zero. Returns false when the saved report cannot be interpreted
    /// safely, in which case the caller must reconnect because BEP 3 offers no way to request the
    /// initial bitfield again.
    /// </summary>
    internal bool TryApplyDeferredAvailability(int pieceCount)
    {
        // A retained connection cannot send a new BEP 3 initial bitfield: that message is only valid
        // as the first post-handshake message. Magnets normally have no local pieces, but if resume
        // data restored some, reconnect so the peer receives an accurate initial bitfield. Legacy
        // Merkle support is likewise fixed when PeerCommunication is constructed and cannot be added
        // safely to an already-negotiated connection.
        if (pieceCount <= 0 || _torrent.Pieces.ReceivedCount > 0 ||
            (_torrent.InfoFile.Info.IsMerkle && UtHashPiece == null))
        {
            return false;
        }

        lock (_availabilityLock)
        {
            if (!HasReportedPieces)
            {
                return false;
            }

            var resized = new PiecesProgress(pieceCount);
            switch (_deferredAvailabilityKind)
            {
                case DeferredAvailabilityKind.HaveAll:
                    resized.SetHaveAll();
                    break;
                case DeferredAvailabilityKind.HaveNone:
                    resized.SetHaveNone();
                    break;
                case DeferredAvailabilityKind.Bitfield:
                    int expectedBytes = (pieceCount + 7) / 8;
                    if (_deferredBitfield is not { } bitfield || bitfield.Length != expectedBytes ||
                        HasNonZeroSpareBits(bitfield, pieceCount))
                    {
                        return false;
                    }
                    resized.FromBitfield(bitfield);
                    break;
                case DeferredAvailabilityKind.None:
                    // A sequence of HAVE messages without a bitfield is valid and starts from empty.
                    break;
            }

            foreach (int index in _deferredHavePieces)
            {
                if ((uint)index >= (uint)pieceCount)
                {
                    return false;
                }
                resized.AddPiece(index);
            }

            PeerPieces = resized;
            _deferredBitfield = null;
            _deferredHavePieces.Clear();
            _deferredAvailabilityKind = DeferredAvailabilityKind.None;
            return true;
        }
    }

    private static bool HasNonZeroSpareBits(byte[] bitfield, int pieceCount)
    {
        int spareBits = 8 - (pieceCount & 7);
        return spareBits < 8 && (bitfield[^1] & ((1 << spareBits) - 1)) != 0;
    }

    internal Task RefreshExtendedHandshakeAfterMetadataAsync() =>
        RemoteSupportsExtensions ? SendExtendedHandshakeAsync() : Task.CompletedTask;

    /// <summary>
    /// BEP 40: Canonical peer priority. Higher values indicate more preferred peers.
    /// </summary>
    public uint Priority { get; set; }

    private System.Net.IPEndPoint? _remoteEndPoint;

    /// <summary>
    /// The remote peer's endpoint. Always stored in normalized form (IPv4-mapped IPv6
    /// addresses are converted to plain IPv4) so it can be used as a dictionary key
    /// regardless of whether the connection came from a dual-stack socket or a tracker/PEX address.
    /// </summary>
    public System.Net.IPEndPoint? RemoteEndPoint
    {
        get => _remoteEndPoint;
        internal set => _remoteEndPoint = NetworkUtils.NormalizeEndPoint(value);
    }

    public ExtensionHandshake? RemoteExtensions { get; private set; }

    /// <summary>
    /// Where this peer accepts connections, once it has told us via BEP 10 <c>p</c>. Null until then,
    /// and null for a peer that says it is not listening. Distinct from <see cref="RemoteEndPoint"/>,
    /// whose port is whatever the connection happened to come from.
    /// </summary>
    public System.Net.IPEndPoint? RemoteListenEndPoint { get; internal set; }

    private byte[]? _ourPeerId;

    /// <summary>
    /// The peer id we present on this connection.
    ///
    /// <para>
    /// Outgoing connections each get a fresh one, as libtorrent does. The eight byte client
    /// fingerprint is kept so peers can still tell what we are; only the unique tail changes. A single
    /// id reused everywhere lets anyone watching a swarm tie all of a client's connections together,
    /// across torrents and across sessions, which is exactly how swarm monitoring works. The stable id
    /// from settings is still what the tracker sees, since that is the identity a tracker session is
    /// built on.
    /// </para>
    /// </summary>
    public byte[] OurPeerId => _ourPeerId ??= _torrent.Settings.PeerId;

    /// <summary>
    /// Whether this peer has let a request expire without answering it, and has sent us nothing since.
    ///
    /// <para>
    /// A snubbed peer is not punished - it keeps its requests and its slot. It is only steered away
    /// from rare pieces, because a stalled peer sitting on the one copy of a piece nobody else has is
    /// what holds a download at 99%. Cleared the moment any block arrives from it.
    /// </para>
    /// </summary>
    public bool IsSnubbed { get; private set; }

    internal void MarkSnubbed()
    {
        if (!IsSnubbed)
        {
            IsSnubbed = true;
            _logger.LogDebug("Peer {PeerName} snubbed: a request expired unanswered", Name);
        }
    }

    internal void ClearSnubbed()
    {
        if (IsSnubbed)
        {
            IsSnubbed = false;
            _logger.LogDebug("Peer {PeerName} no longer snubbed: data received", Name);
        }
    }

    public bool RemoteSupportsExtensions { get; private set; }

    /// <summary>
    /// BEP-6 Fast Extension support. Enables HaveAll, HaveNone, Suggest, AllowedFast, Reject messages.
    /// </summary>
    public bool RemoteSupportsFastExtension { get; private set; }

    /// <summary>
    /// BEP 21: the peer told us it is not interested in downloading anything, so an upload slot spent
    /// on it is wasted.
    /// </summary>
    public bool RemoteIsUploadOnly { get; private set; }

    /// <summary>
    /// BEP-52 BitTorrent v2 support. Indicates peer can handle v2 info hashes and Merkle trees.
    /// </summary>
    public bool RemoteSupportsV2 { get; internal set; }

    public long SmoothedDownloadSpeed => Volatile.Read(ref _smoothedDownloadSpeed);

    public int SmoothedRttMs => Interlocked.CompareExchange(ref _smoothedRttMs, 0, 0);

    public int Strikes
    {
        get => _strikes;
        set => Interlocked.Exchange(ref _strikes, value);
    }

    public long Uploaded => Interlocked.Read(ref _uploaded);

    public long UploadSpeed { get; private set; }

    /// <summary>
    /// BEP 30: ut_hash_piece extension for Merkle hash torrents.
    /// </summary>
    public UtHashPiece? UtHashPiece { get; }

    IUtHashPiece? IPeerCommunication.UtHashPiece => UtHashPiece;

    public UtHolepunch UtHolepunch { get; }

    IUtHolepunch IPeerCommunication.UtHolepunch => UtHolepunch;

    /// <summary>BEP 54: piece retraction for this peer.</summary>
    public LtDontHave LtDontHave { get; }

    public UtMetadata UtMetadata { get; }

    IUtMetadata IPeerCommunication.UtMetadata => UtMetadata;

    public virtual IUtPex UtPex { get; }

    IUtPex IPeerCommunication.UtPex => UtPex;

    internal TcpClient? Client { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint", "S2292:Trivial properties should be auto-implemented", Justification = "Backing field used with Interlocked")]
    internal int Connected { get => _connected; set => _connected = value; }

    /// <summary>Test hook: overrides the smoothed download speed used by choke/transport decisions.</summary>
    internal void SetSmoothedDownloadSpeedForTesting(long speed) => Volatile.Write(ref _smoothedDownloadSpeed, speed);

    /// <summary>Test hook: overrides the remote peer's interested state.</summary>
    internal void SetPeerInterestedForTesting(bool interested) => Volatile.Write(ref _peerInterested, interested ? 1 : 0);

    /// <summary>Test hook: overrides the local interested state used by peer-health policy.</summary>
    internal void SetAmInterestedForTesting(bool interested) => Volatile.Write(ref _amInterested, interested ? 1 : 0);

    /// <summary>Test hook: overrides whether the remote peer is currently choking us.</summary>
    internal void SetPeerChokingForTesting(bool choking) => Volatile.Write(ref _peerChoking, choking ? 1 : 0);

    /// <summary>Test hook: overrides the activity timestamp used by idle-timeout policy.</summary>
    internal void SetLastActivityTicksForTesting(long ticks) => Interlocked.Exchange(ref _lastActivityTicksValue, ticks);

    internal void SetLastSentTicksForTesting(long ticks) => Interlocked.Exchange(ref _lastSentTicksValue, ticks);

    /// <summary>
    /// True if the local side initiated this connection (outgoing dial). Used by PeerManager
    /// for the duplicate-peer-id tie-break on crossed (simultaneous-open) connections.
    /// </summary>
    internal bool IsOutgoing { get; set; }

    /// <summary>
    /// Whether this connection moved payload that made the torrent useful. Unlike the byte counters,
    /// this also covers extension payload such as magnet metadata.
    /// </summary>
    internal bool HasExchangedUsefulData => Volatile.Read(ref _usefulDataExchanged) != 0;

    internal void MarkUsefulDataExchanged() => Volatile.Write(ref _usefulDataExchanged, 1);

    private Stream? _stream;

    /// <summary>
    /// The peer connection, always metered.
    ///
    /// <para>
    /// The setter wraps whatever it is handed in a <see cref="RateLimitedStream"/>. Doing it here rather
    /// than at each assignment is deliberate: raw sockets, proxied streams and uTP streams are assigned
    /// from half a dozen places across connect, accept and handshake paths, and a limiter that has to be
    /// remembered at every one of them is a limiter that will eventually be forgotten - which is exactly
    /// how it came to apply only to encrypted connections.
    /// </para>
    /// </summary>
    internal Stream? Stream
    {
        get => _stream;
        set => _stream = WrapRateLimited(value);
    }

    /// <summary>
    /// Wraps a freshly assigned stream in the rate limiter, unless it is already metered. An
    /// <see cref="EncryptedStream"/> is only ever constructed over the current <see cref="Stream"/>,
    /// so by the time one is assigned the limiter is already underneath it.
    /// </summary>
    private Stream? WrapRateLimited(Stream? stream)
    {
        if (stream is null or RateLimitedStream or EncryptedStream)
        {
            return stream;
        }

        string hash = _torrent.Hash.ToHexStringUpper();

        return new RateLimitedStream(
            stream,
            this,
            _torrent.Bandwidth,
            [BandwidthManager.GlobalDownload, $"{hash}_DL"],
            [BandwidthManager.GlobalUpload, $"{hash}_UL"],

            // Owns the stream it wraps: CleanupResourcesAsync disposes Stream and expects that to
            // close the connection. Client and UtpStream are disposed separately too, which is a
            // harmless second call, but a bare stream has no such owner and would otherwise leak.
            leaveInnerOpen: false);
    }

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
        if (bytes > 0)
        {
            MarkUsefulDataExchanged();
        }

        // Any data at all clears the snub: the peer answered, so whatever stalled it has passed.
        ClearSnubbed();
    }

    public void AddUploaded(long bytes)
    {
        Interlocked.Add(ref _uploaded, bytes);
        if (bytes > 0)
        {
            MarkUsefulDataExchanged();
        }
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

    /// <param name="closedBy">
    /// Filled in by the compiler with the name of the method that closed this connection. Free - it is
    /// a literal at the call site - and it answers the question the stack trace below was being turned
    /// on for. Without it a close records only that it happened: three separate investigations into
    /// peers vanishing seconds after a successful handshake each stalled here, because "Closing
    /// connection to X" is equally consistent with the peer hanging up, a duplicate being resolved, a
    /// limit being enforced, and a bug.
    /// </param>
    public async virtual Task CloseAsync([System.Runtime.CompilerServices.CallerMemberName] string? closedBy = null)
    {
        bool wasConnected = Interlocked.Exchange(ref _connected, 0) == 1;

        // Who called CloseAsync is a question for someone debugging a specific teardown, so it is gated
        // on Trace rather than Debug. Debug is the level a consumer turns on to investigate something,
        // which made this fire exactly when it hurt most: closing connections is the most common event
        // on a public swarm, and at roughly thirty frames apiece these traces were 4,848 of the 8,872
        // lines in a ninety-second log. Walking the stack is not free either, and it ran on every close.
        if (wasConnected && _logger.IsEnabled(LogLevel.Trace))
        {
            var stack = new StackTrace();
            _logger.LogTrace("Closing connection to {PeerName} (by {ClosedBy}). wasConnected=true. Trace: {Trace}", Name, closedBy, stack.ToString());
        }
        else if (wasConnected)
        {
            _logger.LogDebug("Closing connection to {PeerName} (by {ClosedBy})", Name, closedBy);
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
        return await ConnectAsync(ip, port, useUtp, DefaultConnectionTimeoutMs, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dials a peer and completes the BitTorrent handshake.
    ///
    /// <para>
    /// <paramref name="offerEncryption"/> decides whether to open with an encryption handshake when the
    /// configured policy leaves it open. The caller decides because the answer belongs to the peer
    /// rather than to any one attempt: it is remembered on the peer's history and alternates until
    /// something works.
    /// </para>
    /// </summary>
    public async virtual Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, bool offerEncryption = true, CancellationToken ct = default)
    {
        _logger.LogDebug("Connecting to {Ip}:{Port} (uTP: {UseUtp}, encryption: {Encryption}, timeout: {Timeout}ms)", ip, port, useUtp, offerEncryption, timeoutMs);
        IsOutgoing = true;

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
                    await UtpStream.ConnectAsync(linkedCts.Token).ConfigureAwait(false);
                    _logger.LogDebug("uTP connection to {Endpoint} successful", endpoint);
                }
            }

            if (!useUtp)
            {
                var proxy = _torrent.Settings.Proxy;
                var bindAddress = _torrent.Settings.Connection.BindAddress;
                if (proxy.Type != ProxyType.None && proxy.ProxyPeers && !string.IsNullOrEmpty(proxy.Host))
                {
                    _logger.LogDebug("Connecting to {Ip}:{Port} via {ProxyType} proxy {ProxyHost}:{ProxyPort}", ip, port, proxy.Type, proxy.Host, proxy.Port);
                    var result = proxy.Type switch
                    {
                        ProxyType.Socks5 => await ProxyHelper.ConnectSocks5Async(ip, port, proxy.Host, proxy.Port, proxy.Username, proxy.Password, _logger, bindAddress, linkedCts.Token).ConfigureAwait(false),
                        ProxyType.Http => await ProxyHelper.ConnectHttpProxyAsync(ip, port, proxy.Host, proxy.Port, proxy.Username, proxy.Password, _logger, bindAddress, linkedCts.Token).ConfigureAwait(false),
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
                    Client = bindAddress == null
                        ? new TcpClient()
                        : new TcpClient(bindAddress.AddressFamily);
                    if (bindAddress != null)
                    {
                        Client.Client.Bind(new IPEndPoint(bindAddress, 0));
                    }
                    ConfigureTcpClient(Client, _torrent.Settings, _logger);
                    await Client.ConnectAsync(ip, port, linkedCts.Token).ConfigureAwait(false);
                    Stream = Client.GetStream();
                    RemoteEndPoint = Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                }
            }

            _connected = 1;
            _logger.LogDebug("Connected to {Ip}:{Port}", ip, port);

            var encryptionSetting = _torrent.Settings.Connection.Encryption;

            // Require and Refuse are absolute. Allow leaves the choice to the caller, which is where the
            // peer's remembered preference lives.
            bool tryEncryption = encryptionSetting switch
            {
                Encryption.Require => true,
                Encryption.Refuse => false,
                _ => offerEncryption
            };

            if (tryEncryption)
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
                    // The peer hung up rather than answering. That is not evidence about encryption:
                    // peers hang up because they are at their connection limit, do not have the torrent,
                    // or already have us - and dialling straight back in plaintext meets the same reason
                    // again. Measured against a live swarm, that immediate retry failed 72 times out of
                    // 77, and one peer was redialled fifteen times.
                    //
                    // Neither reference implementation retries within an attempt. libtorrent alternates
                    // its per-peer pe_support flag across separate connections; Transmission gives up and
                    // marks a peer that sent nothing back as unconnectable. We follow libtorrent: report
                    // the failure and let the peer's history choose plaintext next time.
                    _logger.LogDebug("Peer {Ip}:{Port} hung up during the encryption handshake", ip, port);
                    await CloseAsync().ConfigureAwait(false);
                    return false;
                }
                // Encryption failed on a still-open connection - fall through and try plaintext on it.
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
        // The catches below report a peer that did not answer, hung up, or refused a handshake. Those
        // are the ordinary outcomes of dialling strangers, and their stack traces are identical every
        // time, so the exception type or message is kept and the trace is not. In one 3.9 MB log,
        // 17,174 of 31,509 lines were traces, most of them from a connect timeout. Genuine faults, in
        // the general catch at the end, still carry theirs.
        catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested)
        {
            // Connection timeout (not explicit cancellation via CloseAsync()) - expected in BitTorrent
            int elapsedMs = GetConnectionElapsedMs();
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect timeout {Ip}:{Port} - peer unresponsive after {Elapsed}ms ({Error})", ip, port, elapsedMs, ex.GetType().Name);
#pragma warning restore S6667
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            // OS-level connection timeout (timeoutMs > OS timeout)
            int elapsedMs = GetConnectionElapsedMs();
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect timeout {Ip}:{Port} - peer unresponsive after {Elapsed}ms ({Error})", ip, port, elapsedMs, ex.GetType().Name);
#pragma warning restore S6667
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Explicit cancellation via CloseAsync()
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect cancelled {Ip}:{Port}", ip, port);
#pragma warning restore S6667
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex)
        {
            // Expected network errors - log without stack trace at Debug level
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect failed {Ip}:{Port} - {Message}", ip, port, ex.Message);
#pragma warning restore S6667
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Expected network errors wrapped in IOException - log without stack trace
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect failed {Ip}:{Port} - {Message}", ip, port, ex.InnerException.Message);
#pragma warning restore S6667
            await CloseAsync().ConfigureAwait(false);
            return false;
        }
        catch (TimeoutException ex)
        {
            // uTP reports an unanswered SYN this way, which is the most ordinary outcome there is of
            // dialling a stranger over UDP - most of the addresses a swarm hands out are behind a NAT
            // that will not answer. It belongs with the timeouts above rather than in the general catch
            // below, where it was being reported as an unexpected fault, at error level, with a stack
            // trace, twice per peer: once here and once from the uTP layer.
            int elapsedMs = GetConnectionElapsedMs();
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Connect timeout {Ip}:{Port} - peer unresponsive after {Elapsed}ms ({Message})", ip, port, elapsedMs, ex.Message);
#pragma warning restore S6667
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

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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
    /// </summary>
    public int GetOptimalPipelineDepth()
    {
        var transferSettings = _torrent.Settings.Transfer;
        int speedBytesPerSec = (int)Math.Min(int.MaxValue, Math.Max(DownloadSpeed, SmoothedDownloadSpeed));
        int rttMs = SmoothedRttMs;

        return PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec,
            rttMs,
            transferSettings.EstimatedBandwidthBytesPerSec,
            transferSettings.EstimatedRttMs,
            transferSettings.InitialPipelineDepth);
    }

    public int GetAdaptivePipelineDepth()
    {
        return PipelineDepthCalculator.Adapt(
            GetOptimalPipelineDepth(),
            Strikes,
            SmoothedRttMs,
            ProtocolConstants.MinPipelineDepth);
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
            msg.Dispose();
            return;
        }

        Interlocked.Exchange(ref _lastSentTicksValue, Environment.TickCount64);

        if (ShouldDropNonCriticalMessage(msg))
        {
            msg.Dispose();
            return;
        }

        try
        {
            if (_sendQueue.TryEnqueue(msg))
            {
                return;
            }

            // Closed rather than full: the connection is going away and this message is never going to be
            // sent. Waiting on it only to be handed ChannelClosedException tells us nothing we do not
            // already know, and shutdown is exactly when the largest batch of messages takes this path.
            if (_sendQueue.IsCompleted)
            {
                msg.Dispose();
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
            msg.Dispose();
        }
        catch (OperationCanceledException ex)
        {
            if (_cts?.IsCancellationRequested == true)
            {
                msg.Dispose();
                throw;
            }

            // Timeout - send queue is backed up, likely network issue
            _logger.LogWarning(ex, "Send queue timeout for {PeerName} - queue backed up, closing connection", Name);
            msg.Dispose();
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
    /// <summary>
    /// BEP 3: "There is also a keepalive message, which is simply a message of length zero."
    ///
    /// <para>
    /// Without these an idle connection looks dead to the remote, which drops it after its own
    /// timeout - two minutes in libtorrent, and Transmission expects traffic on a 100 second cadence.
    /// A seed with nothing to say is exactly the case that goes quiet, so this is what keeps long
    /// lived seeding connections alive.
    /// </para>
    /// </summary>
    public Task SendKeepAliveAsync()
    {
        return SendMessageAsync(new PeerMessage(MessageId.KeepAlive));
    }

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
        if (!PeerHandshake.TryParse(handshake, _torrent.InfoFile.Info, out var parsed))
        {
            _logger.LogWarning("Invalid handshake from {PeerName}: {Reason}", Name, parsed.Error);
            return false;
        }

        _handshakePreRead = true;
        _preReadHandshake = handshake;

        // Extract reserved bytes flags (bytes 20-27)
        RemoteSupportsExtensions = parsed.SupportsExtensions;
        RemoteSupportsFastExtension = parsed.SupportsFastExtension;
        RemoteSupportsV2 = parsed.SupportsV2;
        _logger.LogDebug("Peer {PeerName} capabilities: extensions={RemoteSupportsExtensions}, fast={RemoteSupportsFastExtension}, v2={RemoteSupportsV2}", Name, RemoteSupportsExtensions, RemoteSupportsFastExtension, RemoteSupportsV2);

        // A handshake carrying an id we issued on an outgoing connection is our own connection coming
        // back to us. Keeping it would spend a slot on a peer that can never have anything we need.
        if (_torrent.IsOurOutgoingPeerId(parsed.PeerId))
        {
            _logger.LogDebug("Dropping {PeerName}: it is our own connection looped back", Name);
            await CloseAsync().ConfigureAwait(false);
            return false;
        }

        Array.Copy(parsed.PeerId, PeerId, 20);
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
            // Layered over the already rate limited Stream, so metering stays on the wire side.
            // Owns it, so disposing Stream cascades encryption -> rate limiter -> socket.
            Stream = new EncryptedStream(Stream, encryption, leaveInnerOpen: false);
            _encryptionHandshakeComplete = true;
        }

        _handshakeLoopTask = RunBackgroundTaskAsync(IncomingHandshakeLoopAsync, "IncomingHandshakeLoop", closeOnCompletion: false, ct: ConnectionToken);
    }

    public void StartAsInitiator(Stream stream)
    {
        // We are dialling out, so this connection gets its own id and the torrent remembers it until
        // the connection ends. An incoming handshake presenting it is our own dial coming back.
        _ourPeerId = _torrent.IssueOutgoingPeerId();

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

        _lastUnchokedAt = now;
        Interlocked.Exchange(ref _uploadedAtLastUnchoke, Uploaded);

        _logger.LogDebug("UNCHOKING peer {PeerName} (speed={DownloadSpeed}B/s, interested={PeerInterested})", Name, DownloadSpeed, PeerInterested);
        _ = SendMessageAsync(new PeerMessage(MessageId.Unchoke));
        _lastChokeChange = now;
    }

    public void UpdateSpeed()
    {
        long totalDown = Downloaded;
        long totalUp = Uploaded;

        DownloadSpeed = Math.Max(0, totalDown - _lastDownloaded);
        UploadSpeed = Math.Max(0, totalUp - _lastUploaded);

        _lastDownloaded = totalDown;
        _lastUploaded = totalUp;

        // Instead of a simple 0.5/0.875 EMA, use a strategy that:
        // 1. Adopts higher speeds quickly (to find peaks)
        // 2. Adopts lower speeds SLOWLY (to ignore momentary stalls/jitter)
        // This prevents the "sawtooth" pattern where one bad second drops the average too much.

        long currentSmoothed, newSmoothed;
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

    /// <summary>
    /// Withdraws a piece the peer offered but then refused, so we stop asking for it.
    ///
    /// <para>
    /// A peer that rejects a request for a piece it had listed as allowed-fast or suggested has changed
    /// its mind - it may have run out of upload slots or dropped the piece. Leaving the offer in place
    /// invites the same request again and the same rejection after it, which is a loop that ends only
    /// when the connection does. libtorrent drops the piece from whichever set it came from on exactly
    /// this signal.
    /// </para>
    /// </summary>
    public void WithdrawOfferedPiece(int pieceIndex, bool fromAllowedFast)
    {
        lock (_fastPiecesLock)
        {
            if (fromAllowedFast)
            {
                if (_allowedFastPieces.Remove(pieceIndex))
                {
                    _allowedFastSnapshot = null;
                }
            }
            else if (_suggestedPieces.Remove(pieceIndex))
            {
                _suggestedSnapshot = null;
            }
        }
    }

    private async Task CleanupResourcesAsync()
    {
        // Nothing can loop back to a connection that is gone, and the set must not grow forever.
        _torrent.ReleaseOutgoingPeerId(_ourPeerId);

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        // Always dispose resources, even if we weren't fully connected
        // This prevents leaks when connection fails during ConnectAsync
        // Disposing Stream cascades through the encryption and rate limiting wrappers to the socket.
        // Client and UtpStream are still disposed below for the paths that own one; both are
        // idempotent, so the second call is a no-op.
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
        return PeerHandshake.Create(_torrent.InfoFile.Info, OurPeerId);
    }

    private int GetAdaptiveSendQueueLimit()
    {
        long speed = SmoothedDownloadSpeed;
        long extra = speed / 200_000 * 50;
        int limit = (int)Math.Min(SendQueueCapacityMax, SendQueueBaseSoftLimit + extra);
        return Math.Clamp(limit, SendQueueCapacityMin, SendQueueCapacityMax);
    }

    private int GetOptimalPipelineDepthForRtt(int rttMs)
    {
        int speedBytesPerSec = (int)Math.Min(int.MaxValue, Math.Max(DownloadSpeed, SmoothedDownloadSpeed));
        return PipelineDepthCalculator.CalculateOptimalForRtt(speedBytesPerSec, rttMs);
    }

    private async Task HandleExtendedMessageAsync(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var span = data.Span;
        byte id = span[0];
        byte[] payload = data.Length > 1 ? data[1..].ToArray() : [];

        if (id == 0) // Handshake
        {
            BDict? dict;
            try
            {
                dict = BencodeParser.Parse(payload) as BDict;
            }
            catch (FormatException ex)
            {
                _logger.LogDebug(ex, "Malformed extended handshake from {PeerName}", Name);
                dict = null;
            }

            if (dict is not null)
            {
                RemoteExtensions = ExtensionHandshake.Parse(dict);
                UtMetadata.Init(RemoteExtensions);
                UtPex.Init(RemoteExtensions);
                UtHolepunch.Init(RemoteExtensions);
                LtDontHave.Init(RemoteExtensions);

                // BEP 21: a peer may re-send its handshake to change this, so it is re-read every
                // time rather than latched on the first one.
                RemoteIsUploadOnly = RemoteExtensions.IsUploadOnly;

                // BEP 30: Initialize ut_hash_piece from remote handshake
                if (UtHashPiece != null && RemoteExtensions.MessageIds.TryGetValue(UtHashPiece.Name, out int hashPieceId))
                {
                    UtHashPiece.RemoteMessageId = (byte)hashPieceId;
                    _logger.LogDebug("BEP 30: Peer {PeerName} supports ut_hash_piece (ID={Id})", Name, hashPieceId);
                }

                // BEP 10 'p': where this peer actually listens, which is not where it connected from.
                if (RemoteExtensions.ListenPort is { } advertisedPort)
                {
                    RemoteListenEndPoint = RemoteEndPoint is null || advertisedPort == 0
                        ? null
                        : new System.Net.IPEndPoint(RemoteEndPoint.Address, advertisedPort);
                    _logger.LogDebug(
                        "Peer {PeerName} listens on {ListenEndPoint}", Name, RemoteListenEndPoint);
                }

                // BEP 10 'yourip': one peer's opinion of our external address. Treated as a vote rather
                // than an answer - any single peer can be wrong or lying, and the tracker already
                // resolves this by agreement.
                if (RemoteExtensions.YourIp is { Length: 4 or 16 } reportedIp)
                {
                    _torrent.ReportExternalAddress(reportedIp);
                }

                _logger.LogDebug("{PeerName} supports extensions: {Extensions}", Name, string.Join(", ", RemoteExtensions.MessageIds.Keys));
                try { await Listener.ExtendedHandshakeFinishedAsync(this, RemoteExtensions).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "ExtendedHandshakeFinished callback error"); }
            }
        }
        else
        {
            if (UtMetadata.LocalMessageId == id)
            {
                try { await Listener.ExtendedMessageReceivedAsync(this, id, payload).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "ExtendedMessageReceived callback error"); }
            }
            else if (UtPex.LocalMessageId == id)
            {
                await UtPex.HandleMessageAsync(payload).ConfigureAwait(false);
            }
            else if (UtHolepunch.LocalMessageId == id)
            {
                await UtHolepunch.HandleMessageAsync(payload).ConfigureAwait(false);
            }
            else if (UtHashPiece?.LocalMessageId.HasValue == true && UtHashPiece.LocalMessageId.Value == id)
            {
                // BEP 30: Handle ut_hash_piece messages
                UtHashPiece.HandleMessage(payload);
            }
            else if (LtDontHave.LocalMessageId == id)
            {
                // BEP 54: the peer lost a piece, so clear it from their bitfield. That is what stops
                // us picking this peer for the piece again and lets the picker source it elsewhere.
                //
                // Requests already in flight for it are left to the existing request timeout rather
                // than being purged here. BEP 54 says they "are silently cancelled, just like when
                // receiving a Choke" - a description of what the peer will do, namely never answer -
                // and reclaiming them early would mean a new hook on IPeerListener for a latency
                // refinement the timeout path already covers.
                LtDontHave.HandleMessage(payload);
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

                // Anything past the handshake belongs to the message stream. Dropping it here left the
                // receive loop starting mid-message.
                if (_plaintextBuffer.Length > 68)
                {
                    _bufferedAfterHandshake = _plaintextBuffer.AsSpan(68).ToArray();
                }
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
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Incoming handshake I/O error for {PeerName} - {Message}", Name, ex.Message);
#pragma warning restore S6667
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
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Outgoing connected handshake I/O error for {PeerName} - {Message}", Name, ex.Message);
#pragma warning restore S6667
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
                catch (OperationCanceledException)
                {
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
                    _logger.LogDebug("Encryption handshake timeout for {PeerName}", Name);
#pragma warning restore S6667
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
                // "stream" is the captured Stream property, so the rate limiter is already underneath,
                // and owning it makes one Dispose cascade down to the socket.
                Stream = new EncryptedStream(
                    stream,
                    pe.Encryption ?? new ProtocolEncryption(),
                    leaveInnerOpen: false);

                if (pe.ReceivedPayload != null)
                {
                    await SetHandshakeReceivedAsync(pe.ReceivedPayload).ConfigureAwait(false);
                }

                return EncryptionHandshakeResult.Success;
            }
        }
        // A network failure part way through the handshake leaves the socket unusable: we have already
        // written MSE bytes to it, and it is now either dead or desynchronised. Report ConnectionClosed
        // rather than Failed so the caller reconnects before trying plaintext.
        //
        // Returning Failed here meant falling through to a plaintext handshake on the broken socket,
        // which could only ever throw - losing a peer that would have connected in plaintext, and
        // surfacing as "Connect failed (unexpected)" further up. Measured against a live swarm: 41 such
        // pairs in two minutes, roughly 3% of all connection attempts.
        catch (SocketException ex)
        {
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Encryption handshake failed for {PeerName} - {Message}", Name, ex.Message);
#pragma warning restore S6667
            return EncryptionHandshakeResult.ConnectionClosed;
        }
        catch (IOException ex)
        {
#pragma warning disable S6667 // Deliberately no stack trace: see note above.
            _logger.LogDebug("Encryption handshake I/O failure for {PeerName} - {Message}", Name, ex.Message);
#pragma warning restore S6667
            return EncryptionHandshakeResult.ConnectionClosed;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Encryption handshake aborted for {PeerName} - connection already disposed", Name);
            return EncryptionHandshakeResult.ConnectionClosed;
        }
        catch (Exception ex)
        {
            // Anything else really is unexpected and worth a stack trace.
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
                lock (_availabilityLock)
                {
                    if (PeerPieces.Count == 0)
                    {
                        _deferredHavePieces.Add(msg.HavePieceIndex);
                    }
                    else
                    {
                        PeerPieces.AddPiece(msg.HavePieceIndex);
                    }
                    HasReportedPieces = true;
                }
                break;

            case MessageId.Bitfield:
                ReadOnlySpan<byte> receivedBitfield = msg.Data.Length > 0 ? msg.Data : msg.Payload.Span;
                lock (_availabilityLock)
                {
                    if (PeerPieces.Count == 0)
                    {
                        _deferredBitfield = receivedBitfield.ToArray();
                        _deferredHavePieces.Clear();
                        _deferredAvailabilityKind = DeferredAvailabilityKind.Bitfield;
                    }
                    else
                    {
                        PeerPieces.FromBitfield(receivedBitfield);
                    }
                    HasReportedPieces = true;
                }
                _logger.LogDebug("{PeerName} sent bitfield: {Count} pieces", Name, PeerPieces.ReceivedCount);
                break;

            case MessageId.HaveAll:
                lock (_availabilityLock)
                {
                    if (PeerPieces.Count == 0)
                    {
                        _deferredBitfield = null;
                        _deferredHavePieces.Clear();
                        _deferredAvailabilityKind = DeferredAvailabilityKind.HaveAll;
                    }
                    else
                    {
                        PeerPieces.SetHaveAll();
                    }
                    HasReportedPieces = true;
                }
                _logger.LogDebug("{PeerName} has ALL pieces (FastExt)", Name);
                break;

            case MessageId.HaveNone:
                lock (_availabilityLock)
                {
                    if (PeerPieces.Count == 0)
                    {
                        _deferredBitfield = null;
                        _deferredHavePieces.Clear();
                        _deferredAvailabilityKind = DeferredAvailabilityKind.HaveNone;
                    }
                    else
                    {
                        PeerPieces.SetHaveNone();
                    }
                    HasReportedPieces = true;
                }
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
                _logger.LogTrace("{PeerName} REJECTED request {PieceIndex}:{BlockOffset}", Name, msg.PieceIndex, msg.BlockOffset);
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
                        msg.Data.Length > 0 ? msg.Data : msg.Payload.Span);

                    _logger.LogDebug(
                        "{PeerName} sent BEP 52 hashes for root {PiecesRoot}; accepted={Accepted}",
                        Name,
                        Convert.ToHexString(msg.HashPiecesRoot),
                        accepted);
                }
                break;

            case MessageId.HashReject:
                _logger.LogDebug("{PeerName} rejected BEP 52 hash request for root {PiecesRoot}", Name, Convert.ToHexString(msg.HashPiecesRoot ?? []));
                break;

            case MessageId.Extended:
                await HandleExtendedMessageAsync(msg.Data.Length > 0 ? msg.Data : msg.Payload).ConfigureAwait(false);
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
                    _bufferedAfterHandshake = _plaintextBuffer.AsSpan(68).ToArray();
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
        // Any I/O failure here is a peer that went away mid-handshake, which is unremarkable in a
        // swarm. Only the ones wrapping a SocketException used to be treated that way, so the rest were
        // logged at Error with a stack trace - and an error log full of ordinary network events is an
        // error log nobody can find real problems in.
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Handshake read failed for {PeerName} - {Message}", Name, ex.Message);
            return false;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Handshake read aborted for {PeerName} - connection already disposed", Name);
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

        // Anything read off the socket along with the handshake has to be seen before the socket itself,
        // or the message stream starts part way through a message.
        // Anything read off the socket along with the handshake has to be seen before the socket itself,
        // or the message stream starts part way through a message.
        var source = _bufferedAfterHandshake.Length > 0
            ? new PrefixedStream(_bufferedAfterHandshake, Stream, leaveInnerOpen: true)
            : Stream;
        _bufferedAfterHandshake = [];

        // leaveOpen: the connection stream is owned by CleanupResourcesAsync, which disposes it to close
        // the connection. Letting the reader dispose it as well was harmless only because that path
        // already tolerated a double dispose.
        //
        // The reader's default buffer is 4 KiB, but the unit this protocol moves is a 16 KiB block plus
        // a 13 byte header, so every block cost at least five reads and five decrypt calls. Holding
        // several whole blocks lets each read take as much as the layers below permit, which they cap
        // at one block.
        //
        // This is a throughput change and nothing more. It was first tried as a mitigation for the
        // Transmission fault in FUTURE_IMPROVEMENTS.md, on the theory that draining a peer faster would
        // keep its send backlog below the size that trips it. One run came back clean and that turned
        // out to be an outlier: repeated runs show the failure rate unchanged within noise. It is kept
        // only because reading at the protocol's natural size is right regardless of that bug.
        var pipeReader = PipeReader.Create(
            source,
            new StreamPipeReaderOptions(bufferSize: 4 * ProtocolConstants.BlockSize, leaveOpen: true));
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

                    try
                    {
                        await ProcessMessageAsync(message).ConfigureAwait(false);
                    }
                    finally
                    {
                        message?.Dispose();
                    }
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
                Client = "PeerSharp",

                // Tell the peer what we will actually take. Left unsaid, clients assume their own
                // default - Transmission 500, libtorrent 2000 - and everything above our real depth
                // comes back rejected.
                RequestQueueDepth = ProtocolConstants.MaxOutstandingRequestsPerPeer,

                // BEP 10 'p'. The configured port may be zero, meaning "any", so this has to come from
                // the listener that knows what was actually bound. Without it a peer we dialled sees
                // only our ephemeral source port and can neither reconnect to us nor tell anyone else
                // how to reach us.
                ListenPort = _torrent.PortListener?.Port is > 0 and var bound ? bound : null,

                // BEP 10 'yourip'. The peer cannot see its own external address; we can, and telling it
                // is how it learns. Costs four bytes and is the same courtesy we want in return.
                YourIp = RemoteEndPoint?.Address.GetAddressBytes()
            };
            handshake.MessageIds[UtMetadata.Name] = 1;
            UtMetadata.SetLocalMessageId(1);

            // BEP 27: Don't advertise PEX support for private torrents
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

            // BEP 54: advertise lt_donthave so peers can tell us when they lose a piece.
            handshake.MessageIds[LtDontHave.Name] = 5;
            LtDontHave.SetLocalMessageId(5);

            // BEP 21: tells the peer we will not be downloading, so it need not spend an upload slot
            // on us. Set whenever everything we intend to fetch is already here - which includes a
            // partial seed, not just a complete torrent.
            if (_torrent.SelectionFinished)
            {
                handshake.IsUploadOnly = true;
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
                    try
                    {
                        var writeStart = _timeProvider.GetUtcNow();
                        await WriteMessageToStreamAsync(msg, token).ConfigureAwait(false);
                        var writeMs = (_timeProvider.GetUtcNow() - writeStart).TotalMilliseconds;

                        // Log slow writes that might indicate bandwidth throttling or network issues
                        if (writeMs > 100)
                        {
                            _logger.LogTrace("Slow send to {PeerName}: {MsgId} took {Elapsed}ms (possible bandwidth throttle or network issue)", Name, msg.Id, Math.Round(writeMs, 1));
                        }
                    }
                    finally
                    {
                        msg.Dispose();
                    }
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
            msg.Dispose();
            return false;
        }

        if (ShouldDropNonCriticalMessage(msg))
        {
            msg.Dispose();
            return false;
        }

        try
        {
            if (_sendQueue.TryEnqueue(msg))
            {
                return true;
            }

            // See SendMessageAsync: closed is a known outcome, not an exceptional one.
            if (_sendQueue.IsCompleted)
            {
                msg.Dispose();
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ConnectionToken);
            await _sendQueue.EnqueueAsync(msg, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            // Expected when the send queue is closed during shutdown.
            msg.Dispose();
            return false;
        }
        catch (OperationCanceledException)
        {
            msg.Dispose();
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
