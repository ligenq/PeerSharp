using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Internals.Trackers;

/// <summary>
/// Exception thrown for UDP tracker protocol errors.
/// </summary>
public class UdpTrackerException : Exception
{
    public UdpTrackerException(string message, bool isTransient = false)
        : base(message)
    {
        IsTransient = isTransient;
    }

    public UdpTrackerException(string message, Exception inner, bool isTransient = false)
        : base(message, inner)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}

internal class UdpTracker : TrackerBase, IDisposable
{
    internal TimeSpan _requestTimeout = DefaultRequestTimeout;

    // Retry configuration for transient failures
    private const int MaxRetries = 3;

    private static readonly TimeSpan ConnectionIdLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 };
    private readonly ILogger<UdpTracker> _logger = TorrentLoggerFactory.CreateLogger<UdpTracker>();
    private readonly IUdpSocketFactory _socketFactory;

    private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

    private readonly TimeProvider _timeProvider;
    private IUdpSocket? _client;

    // BEP 15: Connection IDs expire after 60 seconds
    private long _connectionId;

    private DateTimeOffset _connectionIdTimestamp = DateTimeOffset.MinValue;
    private AtomicDisposal _disposal = new();
    private IPEndPoint? _endpoint;
    private TcpClient? _proxyControlClient; // Keep TCP connection alive for SOCKS5 UDP association
    private IPEndPoint? _proxyUdpEndPoint;

    public UdpTracker(TimeProvider timeProvider)
        : this(timeProvider, new UdpSocketFactory())
    {
    }

    internal UdpTracker(TimeProvider timeProvider, IUdpSocketFactory socketFactory)
    {
        _timeProvider = timeProvider;
        _socketFactory = socketFactory;
    }

    public override async Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Retry logic for transient failures
            Exception? lastException = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        // Exponential backoff delay before retry
                        int delayMs = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                        _logger.LogDebug("Announce retry {Attempt}/{MaxRetries} after {Delay}ms delay", attempt, MaxRetries, delayMs);
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, ct).ConfigureAwait(false);

                        // Reset client on retry to get fresh connection
                        _client?.Close();
                        _client = null;
                        _proxyControlClient?.Dispose();
                        _proxyControlClient = null;
                        _connectionId = 0;
                    }

                    await EnsureConnectedAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    long connId = await GetConnectionIdAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    var response = await SendAnnounceAsync(connId, evt, ct).ConfigureAwait(false);

                    RaiseAnnounceResult(true, response);
                    return;
                }
                catch (UdpTrackerException ex) when (ex.IsTransient && attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Announce transient error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
                }
                catch (TimeoutException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Announce timeout (attempt {Attempt}/{MaxRetries})", attempt + 1, MaxRetries);
                }
                catch (SocketException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Announce socket error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
                }
            }

            // All retries exhausted - this is expected for unreachable trackers
            _connectionId = 0;
            _logger.LogWarning(lastException, "Tracker {Url} announce failed after {MaxRetries} retries - {Message}", Url, MaxRetries, lastException?.Message ?? "Unknown error");
            RaiseAnnounceResult(false, new AnnounceResponse(), lastException?.Message ?? "Retries exhausted");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UdpTrackerException ex)
        {
            // Non-transient tracker error (e.g., protocol error)
            _connectionId = 0;
            _logger.LogWarning(ex, "Tracker {Url} announce failed - {Message}", Url, ex.Message);
            RaiseAnnounceResult(false, new AnnounceResponse(), ex.Message);
        }
        catch (Exception ex) when (ex is SocketException || ex is TimeoutException)
        {
            // Expected network errors on first attempt (no retries)
            _connectionId = 0;
            _logger.LogWarning(ex, "Tracker {Url} announce failed - {Message}", Url, ex.Message);
            RaiseAnnounceResult(false, new AnnounceResponse(), ex.Message);
        }
        catch (Exception ex)
        {
            // Unexpected exception
            _connectionId = 0;
            _logger.LogError(ex, "Tracker {Url} announce failed (unexpected)", Url);
            RaiseAnnounceResult(false, new AnnounceResponse(), ex.Message);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // Exponential backoff
    public override void Deinit()
    {
        _client?.Close();
        _client = null;
        _proxyControlClient?.Dispose();
        _proxyControlClient = null;
        _connectionId = 0;
        _connectionIdTimestamp = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public override async Task ScrapeAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Retry logic for transient failures
            Exception? lastException = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        // Exponential backoff delay before retry
                        int delayMs = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                        _logger.LogDebug("Scrape retry {Attempt}/{MaxRetries} after {Delay}ms delay", attempt, MaxRetries, delayMs);
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, ct).ConfigureAwait(false);

                        // Reset client on retry to get fresh connection
                        _client?.Close();
                        _client = null;
                        _connectionId = 0;
                    }

                    await EnsureConnectedAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    long connId = await GetConnectionIdAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    var response = await SendScrapeAsync(connId, ct).ConfigureAwait(false);

                    RaiseScrapeResult(true, response);
                    return;
                }
                catch (UdpTrackerException ex) when (ex.IsTransient && attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Scrape transient error (attempt {Attempt})", attempt + 1);
                }
                catch (TimeoutException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Scrape timeout (attempt {Attempt})", attempt + 1);
                }
                catch (SocketException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Scrape socket error (attempt {Attempt})", attempt + 1);
                }
            }

            // All retries exhausted
            _connectionId = 0;
            _logger.LogWarning(lastException, "Tracker {Url} scrape failed after {MaxRetries} retries - {Message}", Url, MaxRetries, lastException?.Message ?? "Unknown error");
            RaiseScrapeResult(false, new ScrapeResponse());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected exception
            _connectionId = 0;
            _logger.LogWarning(ex, "Tracker {Url} scrape failed", Url);
            RaiseScrapeResult(false, new ScrapeResponse());
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public override async Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct)
    {
        if (infoHashes == null || infoHashes.Count == 0)
        {
            return;
        }

        var hashes = infoHashes
            .Where(h => h.Length == InfoHash.V1Length)
            .Select(h => h.Span.ToArray())
            .ToList();

        if (hashes.Count == 0)
        {
            return;
        }

        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? lastException = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        int delayMs = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                        _logger.LogDebug("Multi-scrape retry {Attempt}/{MaxRetries} after {Delay}ms delay", attempt, MaxRetries, delayMs);
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, ct).ConfigureAwait(false);

                        _client?.Close();
                        _client = null;
                        _connectionId = 0;
                    }

                    await EnsureConnectedAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    long connId = await GetConnectionIdAsyncUnsafeAsync(ct).ConfigureAwait(false);

                    var response = await SendScrapeMultipleAsync(connId, hashes, ct).ConfigureAwait(false);
                    RaiseMultiScrapeResult(true, response);
                    return;
                }
                catch (UdpTrackerException ex) when (ex.IsTransient && attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Multi-scrape transient error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
                }
                catch (TimeoutException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Multi-scrape timeout (attempt {Attempt}/{MaxRetries})", attempt + 1, MaxRetries);
                }
                catch (SocketException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogDebug(ex, "Multi-scrape socket error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
                }
            }

            _connectionId = 0;
            _logger.LogWarning(lastException, "Tracker {Url} multi-scrape failed after {MaxRetries} retries - {Message}", Url, MaxRetries, lastException?.Message ?? "Unknown error");
            RaiseMultiScrapeResult(false, new MultiScrapeResponse());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UdpTrackerException ex)
        {
            _connectionId = 0;
            _logger.LogWarning(ex, "Tracker {Url} multi-scrape failed - {Message}", Url, ex.Message);
            RaiseMultiScrapeResult(false, new MultiScrapeResponse());
        }
        catch (Exception ex) when (ex is SocketException || ex is TimeoutException)
        {
            _connectionId = 0;
            _logger.LogWarning(ex, "Tracker {Url} multi-scrape failed - {Message}", Url, ex.Message);
            RaiseMultiScrapeResult(false, new MultiScrapeResponse());
        }
        catch (Exception ex)
        {
            _connectionId = 0;
            _logger.LogError(ex, "Tracker {Url} multi-scrape failed (unexpected)", Url);
            RaiseMultiScrapeResult(false, new MultiScrapeResponse());
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// BEP 48: Scrape multiple info hashes in a single request.
    /// UDP trackers support up to ~74 hashes per request (limited by packet size).
    /// </summary>
    public async Task<MultiScrapeResponse> ScrapeMultipleAsync(IList<byte[]> infoHashes, CancellationToken ct = default)
    {
        if (infoHashes == null || infoHashes.Count == 0)
        {
            return new MultiScrapeResponse();
        }

        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? lastException = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        int delayMs = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                        _logger.LogDebug("Multi-scrape retry {Attempt}/{MaxRetries} after {Delay}ms delay", attempt, MaxRetries, delayMs);
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, ct).ConfigureAwait(false);

                        _client?.Close();
                        _client = null;
                        _connectionId = 0;
                    }

                    await EnsureConnectedAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    long connId = await GetConnectionIdAsyncUnsafeAsync(ct).ConfigureAwait(false);
                    return await SendScrapeMultipleAsync(connId, infoHashes, ct).ConfigureAwait(false);
                }
                catch (UdpTrackerException ex) when (ex.IsTransient && attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Multi-scrape transient error (attempt {Attempt})", attempt + 1);
                }
                catch (TimeoutException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Multi-scrape timeout (attempt {Attempt})", attempt + 1);
                }
                catch (SocketException ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Multi-scrape socket error (attempt {Attempt})", attempt + 1);
                }
            }

            _connectionId = 0;
            _logger.LogError(lastException, "Tracker {Url} multi-scrape failed after {MaxRetries} retries", Url, MaxRetries);
            return new MultiScrapeResponse();
        }
        catch (Exception ex)
        {
            _connectionId = 0;
            _logger.LogError(ex, "Tracker {Url} multi-scrape failed", Url);
            return new MultiScrapeResponse();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    internal void SetRequestTimeout(TimeSpan timeout)
    {
        _requestTimeout = timeout;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            Deinit();
            _syncLock.Dispose();
        }
    }

    private async Task<long> ConnectAsync(CancellationToken ct)
    {
        if (_client == null || _endpoint == null)
        {
            throw new InvalidOperationException("UDP tracker client not initialized - call EnsureConnectedAsyncUnsafe first");
        }

        int transId = Random.Shared.Next();

        // Request: ProtocolId (8) + Action (4) + TransId (4)
        byte[] req = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(0), 0x41727101980);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(8), 0); // Action Connect
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(12), transId);

        await SendPacketAsync(req, ct).ConfigureAwait(false);

        // Response: Action (4) + TransId (4) + ConnId (8)
        var res = await ReceiveSpecificTransactionAsync(transId, 16, ct).ConfigureAwait(false);

        int action = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(0));
        int resTransId = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(4));

        if (resTransId != transId)
        {
            throw new InvalidDataException($"Invalid connect response: transaction ID mismatch (expected {transId}, got {resTransId})");
        }

        if (action == 3)
        {
            throw new UdpTrackerException($"Tracker returned error on connect: {ParseTrackerErrorMessage(res.Buffer)}", isTransient: false);
        }

        if (action != 0)
        {
            throw new InvalidDataException($"Invalid connect response: expected action 0, got {action}");
        }

        return BinaryPrimitives.ReadInt64BigEndian(res.Buffer.AsSpan(8));
    }

    private async Task EnsureConnectedAsyncUnsafeAsync(CancellationToken ct)
    {
        if (_client == null)
        {
            var uri = new Uri(Url);
            var proxy = Torrent.Settings.Proxy;

            try
            {
                if (proxy.Type == ProxyType.Socks5 && proxy.ProxyTrackers && !string.IsNullOrEmpty(proxy.Host))
                {
                    _logger.LogDebug("Connecting to UDP tracker {Url} via SOCKS5 proxy {ProxyHost}:{ProxyPort}", Url, proxy.Host, proxy.Port);
                    var result = await ProxyHelper.ConnectSocks5UdpAsync(proxy.Host, proxy.Port, proxy.Username, proxy.Password, ct).ConfigureAwait(false);
                    _client = new UdpSocketAdapter(result.UdpClient, true);
                    _proxyUdpEndPoint = result.ProxyUdpEndPoint;
                    _proxyControlClient = result.ControlClient;

                    var ips = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                    var preferredIp = ips.FirstOrDefault(ip => ip.AddressFamily == result.ProxyUdpEndPoint.AddressFamily) ?? ips[0];
                    _endpoint = new IPEndPoint(preferredIp, uri.Port);
                }
                else
                {
                    var ips = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                    if (ips.Length == 0)
                    {
                        throw new UdpTrackerException($"DNS resolution failed: no addresses found for {uri.Host}", isTransient: true);
                    }

                    var preferredIp = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ips[0];
                    _endpoint = new IPEndPoint(preferredIp, uri.Port);
                    _client = _socketFactory.Create(_endpoint.AddressFamily);
                }
            }
            catch (SocketException ex)
            {
                throw new UdpTrackerException($"DNS resolution failed for {uri.Host}: {ex.Message}", ex, isTransient: true);
            }
        }
    }

    // Note: Assumes _syncLock is already held by the caller
    private async Task<long> GetConnectionIdAsyncUnsafeAsync(CancellationToken ct)
    {
        // BEP 15: Connection ID expires after 60 seconds
        if (_connectionId != 0 && (_timeProvider.GetUtcNow() - _connectionIdTimestamp) < ConnectionIdLifetime)
        {
            return _connectionId;
        }

        _connectionId = await ConnectAsync(ct).ConfigureAwait(false);
        _connectionIdTimestamp = _timeProvider.GetUtcNow();
        return _connectionId;
    }

    internal static string ParseTrackerErrorMessage(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length <= 8)
        {
            return "(no error message)";
        }

        var messageBytes = buffer[8..];
        int end = messageBytes.Length;
        while (end > 0 && messageBytes[end - 1] == 0)
        {
            end--;
        }

        if (end == 0)
        {
            return "(empty error message)";
        }

        return Encoding.ASCII.GetString(messageBytes[..end]);
    }

    private async Task<UdpReceiveResult> ReceiveSpecificTransactionAsync(int expectedTransId, int minSize, CancellationToken ct)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        var now = _timeProvider.GetUtcNow();
        while ((_timeProvider.GetUtcNow() - now) < _requestTimeout)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_requestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                var res = await _client.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);

                var buffer = res.Buffer;
                if (_proxyUdpEndPoint != null)
                {
                    var unwrapped = ProxyHelper.UnwrapSocks5UdpPacket(buffer);
                    if (unwrapped.Payload.IsEmpty)
                    {
                        continue;
                    }

                    buffer = unwrapped.Payload.ToArray();
                    // Use the unwrapped remote endpoint if needed, but for tracker response
                    // we usually just care about the transaction ID in the payload.
                }

                if (buffer.Length >= 8) // Header is at least 8 bytes (Action + TransID)
                {
                    int receivedTransId = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4));
                    if (receivedTransId == expectedTransId)
                    {
                        int receivedAction = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(0));
                        // BEP 15 action==3 is an error response and may be shorter than the
                        // minSize expected for a successful response. Let the caller surface it.
                        if (buffer.Length < minSize && receivedAction != 3)
                        {
                            throw new InvalidDataException("Response too short");
                        }

                        return _proxyUdpEndPoint != null ? new UdpReceiveResult(buffer, res.RemoteEndPoint) : res;
                    }
                    // Else: Stale packet, ignore and loop
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        throw new TimeoutException();
    }

    private async Task<AnnounceResponse> SendAnnounceAsync(long connId, TrackerEvent evt, CancellationToken ct)
    {
        if (_client == null || _endpoint == null)
        {
            throw new InvalidOperationException("UDP tracker client not initialized - call EnsureConnectedAsyncUnsafe first");
        }

        int transId = Random.Shared.Next();

        // Request:
        // ConnId (8)
        // Action (4) = 1
        // TransId (4)
        // InfoHash (20)
        // PeerId (20)
        // Downloaded (8)
        // Left (8)
        // Uploaded (8)
        // Event (4)
        // IP (4) = 0
        // Key (4)
        // NumWant (4) = -1
        // Port (2)

        byte[] req = new byte[98];
        var span = req.AsSpan();

        BinaryPrimitives.WriteInt64BigEndian(span[..], connId);
        BinaryPrimitives.WriteInt32BigEndian(span[8..], 1); // Action Announce
        BinaryPrimitives.WriteInt32BigEndian(span[12..], transId);
        Torrent.InfoFile.Info.GetTrackerInfoHash().CopyTo(span[16..]);
        Torrent.Settings.PeerId.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt64BigEndian(span[56..], Torrent.FileTransfer.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(span[64..], Torrent.DataLeft);
        BinaryPrimitives.WriteInt64BigEndian(span[72..], Torrent.FileTransfer.Uploaded);

        int eventId = 0;
        if (evt == TrackerEvent.Completed)
        {
            eventId = 1;
        }
        else if (evt == TrackerEvent.Started)
        {
            eventId = 2;
        }
        else if (evt == TrackerEvent.Stopped)
        {
            eventId = 3;
        }

        BinaryPrimitives.WriteInt32BigEndian(span[80..], eventId); // Event
        BinaryPrimitives.WriteInt32BigEndian(span[84..], 0); // IP Default
        BinaryPrimitives.WriteInt32BigEndian(span[88..], Random.Shared.Next()); // Key
        int numwant = (int)Torrent.Settings.MaxPeersPerTrackerRequest;
        if (numwant <= 0)
        {
            numwant = -1;
        }
        BinaryPrimitives.WriteInt32BigEndian(span[92..], numwant);
        BinaryPrimitives.WriteUInt16BigEndian(span[96..], Torrent.Settings.Connection.TcpPort);

        await SendPacketAsync(req, ct).ConfigureAwait(false);

        // Response:
        // Action (4)
        // TransId (4)
        // Interval (4)
        // Leechers (4)
        // Seeders (4)
        // Peers (6 * N for IPv4, 18 * N for IPv6) - BEP 7/15

        var res = await ReceiveSpecificTransactionAsync(transId, 20, ct).ConfigureAwait(false); // Min size
        int action = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(0));
        int resTransId = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(4));

        if (resTransId != transId)
        {
            throw new UdpTrackerException($"Transaction ID mismatch: expected {transId}, got {resTransId}", isTransient: true);
        }

        if (action == 3) // Error
        {
            throw new UdpTrackerException($"Tracker returned error on announce: {ParseTrackerErrorMessage(res.Buffer)}", isTransient: false);
        }
        if (action != 1)
        {
            throw new InvalidDataException($"Invalid announce action: expected 1, got {action}");
        }

        if (res.Buffer.Length < 20)
        {
            throw new InvalidDataException($"Announce response too short: {res.Buffer.Length} bytes");
        }

        var announceResp = new AnnounceResponse
        {
            Interval = (uint)BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(8)),
            LeechCount = (uint)BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(12)),
            SeedCount = (uint)BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(16))
        };

        // BEP 7/15: Peer format depends on tracker address family
        // IPv4: 6 bytes per peer (4 IP + 2 port)
        // IPv6: 18 bytes per peer (16 IP + 2 port)
        int peersLen = res.Buffer.Length - 20;
        bool isIPv6 = _endpoint?.AddressFamily == AddressFamily.InterNetworkV6;
        int peerSize = isIPv6 ? 18 : 6;
        int ipSize = isIPv6 ? 16 : 4;

        for (int i = 0; i + peerSize <= peersLen; i += peerSize)
        {
            var ip = new IPAddress(res.Buffer.AsSpan(20 + i, ipSize).ToArray());
            int port = BinaryPrimitives.ReadUInt16BigEndian(res.Buffer.AsSpan(20 + i + ipSize));
            announceResp.Peers.Add(new IPEndPoint(ip, port));
        }

        return announceResp;
    }

    private async Task SendPacketAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_client == null || _endpoint == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        if (_proxyUdpEndPoint != null)
        {
            int headerLength = _endpoint.AddressFamily == AddressFamily.InterNetwork ? 10 : 22;
            int totalLength = headerLength + data.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
            try
            {
                ProxyHelper.WriteSocks5UdpPacket(data.Span, _endpoint, buffer.AsSpan(0, totalLength));
                await _client.SendAsync(buffer.AsMemory(0, totalLength), _proxyUdpEndPoint, ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            await _client.SendAsync(data, _endpoint, ct).ConfigureAwait(false);
        }
    }

    private async Task<ScrapeResponse> SendScrapeAsync(long connId, CancellationToken ct)
    {
        // Single-hash scrape delegates to multi-hash implementation
        var trackerHash = Torrent.InfoFile.Info.GetTrackerInfoHash();
        var infoHashes = new List<byte[]> { trackerHash.ToArray() };
        var multiResponse = await SendScrapeMultipleAsync(connId, infoHashes, ct).ConfigureAwait(false);

        var hashKey = trackerHash.ToHexStringUpper();
        if (multiResponse.Results.TryGetValue(hashKey, out var result))
        {
            return result;
        }

        return new ScrapeResponse();
    }

    /// <summary>
    /// BEP 48: Send scrape request for multiple info hashes.
    /// </summary>
    private async Task<MultiScrapeResponse> SendScrapeMultipleAsync(long connId, IList<byte[]> infoHashes, CancellationToken ct)
    {
        if (_client == null || _endpoint == null)
        {
            throw new InvalidOperationException("UDP tracker client not initialized - call EnsureConnectedAsyncUnsafe first");
        }

        int transId = Random.Shared.Next();
        int hashCount = Math.Min(infoHashes.Count, UdpTrackerScrapeCodec.MaxHashesPerRequest);
        byte[] req = UdpTrackerScrapeCodec.BuildRequest(connId, transId, infoHashes);

        await SendPacketAsync(req, ct).ConfigureAwait(false);

        // BEP 48: Response format for multiple hashes:
        // Action (4)
        // TransId (4)
        // [Seeders (4) + Completed (4) + Leechers (4)] * N
        int minSize = 8 + (hashCount * 12);
        var res = await ReceiveSpecificTransactionAsync(transId, minSize, ct).ConfigureAwait(false);
        return UdpTrackerScrapeCodec.ParseResponse(res.Buffer, transId, infoHashes);
    }
}
