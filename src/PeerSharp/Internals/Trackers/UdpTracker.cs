using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utilities;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using PeerSharp.Exceptions;

namespace PeerSharp.Internals.Trackers;

internal class UdpTracker : TrackerBase, IDisposable
{
    internal TimeSpan _requestTimeout = DefaultRequestTimeout;

    // Retry configuration for transient failures
    private const int MaxRetries = 3;

    private static readonly TimeSpan ConnectionIdLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly int[] RetryDelaysMs = [1000, 2000, 4000];
    private readonly ILogger<UdpTracker> _logger;
    private readonly IUdpSocketFactory _socketFactory;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAddressesAsync;

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly TimeProvider _timeProvider;
    private IUdpSocket? _client;

    // BEP 15: Connection IDs expire after 60 seconds
    private long _connectionId;

    private DateTimeOffset _connectionIdTimestamp = DateTimeOffset.MinValue;
    private AtomicDisposal _disposal = new();
    private IPEndPoint? _endpoint;
    private string? _proxyTargetHost;
    private AddressFamily? _connectedAddressFamily;
    private AddressFamily? _requestedAddressFamily;
    private TcpClient? _proxyControlClient; // Keep TCP connection alive for SOCKS5 UDP association
    private IPEndPoint? _proxyUdpEndPoint;

    public UdpTracker(TimeProvider timeProvider)
        : this(timeProvider, new UdpSocketFactory(), NullLoggerFactory.Instance)
    {
    }

    public UdpTracker(TimeProvider timeProvider, ILoggerFactory loggerFactory)
        : this(timeProvider, new UdpSocketFactory(), loggerFactory)
    {
    }

    public UdpTracker(TimeProvider timeProvider, ILoggerFactory loggerFactory, HostAddressCache hostAddresses)
        : this(timeProvider, new UdpSocketFactory(), loggerFactory, hostAddresses.ResolveAsync)
    {
    }

    internal UdpTracker(TimeProvider timeProvider, IUdpSocketFactory socketFactory)
        : this(timeProvider, socketFactory, NullLoggerFactory.Instance)
    {
    }

    internal UdpTracker(TimeProvider timeProvider, IUdpSocketFactory socketFactory, ILoggerFactory loggerFactory)
        : this(timeProvider, socketFactory, loggerFactory, ResolveAddressesAsync)
    {
    }

    internal UdpTracker(
        TimeProvider timeProvider,
        IUdpSocketFactory socketFactory,
        ILoggerFactory loggerFactory,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAddressesAsync)
    {
        _timeProvider = timeProvider;
        _socketFactory = socketFactory;
        _logger = loggerFactory.CreateLogger<UdpTracker>();
        _resolveAddressesAsync = resolveAddressesAsync;
    }

    /// <summary>
    /// A name that does not resolve comes back as no addresses, the way <see cref="HostAddressCache"/>
    /// reports it, so both resolvers mean the same thing by an empty answer.
    /// </summary>
    private static async Task<IPAddress[]> ResolveAddressesAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    public override async Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var responses = new List<AnnounceResponse>();
            var errors = new List<Exception>();
            var families = await GetReachableAddressFamiliesAsync(ct).ConfigureAwait(false);
            if (families.Count == 0)
            {
                // Known before sending anything, so it is reported as the result it is rather than thrown
                // and caught once per family and retry.
                _connectionId = 0;
                _logger.LogDebug("Tracker {Url} announce skipped - its host has no usable address", Url);
                RaiseAnnounceResult(false, new AnnounceResponse(), $"Tracker host {new Uri(Url).Host} has no usable address");
                return;
            }

            foreach (var addressFamily in families)
            {
                if (_client != null && _connectedAddressFamily != addressFamily)
                {
                    ResetClientUnsafe();
                }
                _requestedAddressFamily = addressFamily;
                try
                {
                    responses.Add(await AnnounceSingleFamilyAsync(evt, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    _logger.LogDebug(
                        ex,
                        "UDP announce to {Url} over {AddressFamily} failed while the other family remains eligible",
                        Url,
                        addressFamily?.ToString() ?? "proxy-selected");
                }
            }

            if (responses.Count > 0)
            {
                RaiseAnnounceResult(true, MergeAnnounceResponses(responses));
            }
            else
            {
                _connectionId = 0;
                string message = errors.LastOrDefault()?.Message ?? "Tracker announce failed";
                _logger.LogWarning(errors.LastOrDefault(), "Tracker {Url} announce failed over all eligible address families - {Message}", Url, message);
                RaiseAnnounceResult(false, new AnnounceResponse(), message);
            }
        }
        finally
        {
            _requestedAddressFamily = null;
            _syncLock.Release();
        }
    }

    private async Task<AnnounceResponse> AnnounceSingleFamilyAsync(TrackerEvent evt, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    int delayMs = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                    _logger.LogDebug("Announce retry {Attempt}/{MaxRetries} after {Delay}ms delay", attempt, MaxRetries, delayMs);
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, ct).ConfigureAwait(false);
                    ResetClientUnsafe();
                }

                await EnsureConnectedAsyncUnsafeAsync(ct).ConfigureAwait(false);
                long connId = await GetConnectionIdAsyncUnsafeAsync(ct).ConfigureAwait(false);
                return await SendAnnounceAsync(connId, evt, ct).ConfigureAwait(false);
            }
            catch (UdpTrackerException ex) when (ex.IsTransient && attempt < MaxRetries)
            {
                _logger.LogDebug(ex, "Announce transient error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
            }
            catch (TimeoutException ex) when (attempt < MaxRetries)
            {
                _logger.LogDebug(ex, "Announce timeout (attempt {Attempt}/{MaxRetries})", attempt + 1, MaxRetries);
            }
            catch (SocketException ex) when (attempt < MaxRetries)
            {
                _logger.LogDebug(ex, "Announce socket error (attempt {Attempt}/{MaxRetries}) - {Message}", attempt + 1, MaxRetries, ex.Message);
            }
        }

        throw new InvalidOperationException("UDP tracker retry loop completed without a response or exception.");
    }

    // Exponential backoff
    public override void Deinit()
    {
        ResetClientUnsafe();
    }

    /// <summary>
    /// The families to announce over, less those the tracker's host has no address in. A tracker with
    /// no AAAA record is the common case, not an error, and asking it over IPv6 anyway used to fail
    /// with an exception on every announce. When a proxy chooses the route, the proxy resolves the
    /// name and nothing is filtered here.
    /// </summary>
    private async Task<IReadOnlyList<AddressFamily?>> GetReachableAddressFamiliesAsync(CancellationToken ct)
    {
        var families = GetAnnounceAddressFamilies();
        if (families.Any(family => family == null))
        {
            return families;
        }

        var addresses = await _resolveAddressesAsync(new Uri(Url).DnsSafeHost, ct).ConfigureAwait(false);
        return [.. families.Where(family => addresses.Any(address => address.AddressFamily == family))];
    }

    private IReadOnlyList<AddressFamily?> GetAnnounceAddressFamilies()
    {
        var proxy = Torrent.Settings.Proxy;
        if (UdpProxyPolicy.Decide(proxy, proxy.ProxyTrackers) != UdpProxyPolicy.Decision.BindDirectly)
        {
            return [null];
        }

        var bindAddress = Torrent.Settings.Connection.BindAddress;
        if (bindAddress != null)
        {
            return [bindAddress.AddressFamily];
        }

        if (IPAddress.TryParse(new Uri(Url).Host, out var literal))
        {
            return [literal.AddressFamily];
        }

        return [AddressFamily.InterNetwork, AddressFamily.InterNetworkV6];
    }

    private static AnnounceResponse MergeAnnounceResponses(IReadOnlyList<AnnounceResponse> responses)
    {
        var merged = new AnnounceResponse
        {
            Interval = responses.Min(response => response.Interval),
            SeedCount = responses.Max(response => response.SeedCount),
            LeechCount = responses.Max(response => response.LeechCount)
        };

        var minIntervals = responses
            .Where(response => response.MinInterval.HasValue)
            .Select(response => response.MinInterval!.Value)
            .ToArray();
        merged.MinInterval = minIntervals.Length == 0 ? null : minIntervals.Min();

        var peers = new HashSet<IPEndPoint>();
        foreach (var response in responses)
        {
            foreach (var peer in response.Peers)
            {
                if (peers.Add(peer))
                {
                    merged.Peers.Add(peer);
                }
            }
        }

        return merged;
    }

    private void ResetClientUnsafe()
    {
        _client?.Close();
        _client = null;
        _proxyControlClient?.Dispose();
        _proxyControlClient = null;
        _proxyUdpEndPoint = null;
        _endpoint = null;
        _proxyTargetHost = null;
        _connectedAddressFamily = null;
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
                        ResetClientUnsafe();
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

                        ResetClientUnsafe();
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

                        ResetClientUnsafe();
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
            var proxyDecision = UdpProxyPolicy.Decide(proxy, proxy.ProxyTrackers);

            if (proxyDecision == UdpProxyPolicy.Decision.Refuse)
            {
                throw new UdpTrackerException(
                    $"A {proxy.Type} proxy is configured for trackers, but it cannot carry UDP tracker traffic. " +
                    "Use a SOCKS5 proxy, disable tracker proxying, or remove this UDP tracker.",
                    isTransient: false);
            }

            try
            {
                if (proxyDecision == UdpProxyPolicy.Decision.TunnelThroughSocks5)
                {
                    _logger.LogDebug("Connecting to UDP tracker {Url} via SOCKS5 proxy {ProxyHost}:{ProxyPort}", Url, proxy.Host, proxy.Port);
                    var result = await ProxyHelper.ConnectSocks5UdpAsync(
                        proxy.Host,
                        proxy.Port,
                        proxy.Username,
                        proxy.Password,
                        _logger,
                        Torrent.Settings.Connection.BindAddress,
                        ct).ConfigureAwait(false);
                    _client = new UdpSocketAdapter(result.UdpClient, true);
                    _connectedAddressFamily = null;
                    _proxyUdpEndPoint = result.ProxyUdpEndPoint;
                    _proxyControlClient = result.ControlClient;

                    if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
                    {
                        _endpoint = new IPEndPoint(literal, uri.Port);
                    }
                    else
                    {
                        // SOCKS5 resolves the target name. The first matching response establishes
                        // its IP address and the peer-list family, without a local DNS lookup.
                        _proxyTargetHost = uri.IdnHost;
                        _endpoint = new IPEndPoint(IPAddress.Any, uri.Port);
                    }
                }
                else
                {
                    var ips = await _resolveAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                    if (ips.Length == 0)
                    {
                        throw new UdpTrackerException($"DNS resolution failed: no addresses found for {uri.Host}", isTransient: true);
                    }

                    var bindAddress = Torrent.Settings.Connection.BindAddress;
                    var preferredFamily = bindAddress?.AddressFamily
                        ?? _requestedAddressFamily
                        ?? AddressFamily.InterNetwork;
                    var preferredIp = ips.FirstOrDefault(ip => ip.AddressFamily == preferredFamily);
                    if (preferredIp == null)
                    {
                        if (bindAddress != null || _requestedAddressFamily != null)
                        {
                            throw new UdpTrackerException(
                                $"Tracker {uri.Host} has no {preferredFamily} address compatible with this announce.",
                                isTransient: false);
                        }

                        preferredIp = ips[0];
                    }
                    _endpoint = new IPEndPoint(preferredIp, uri.Port);
                    _client = _socketFactory.Create(_endpoint.AddressFamily);
                    _connectedAddressFamily = _endpoint.AddressFamily;
                    if (bindAddress != null)
                    {
                        _client.Client.Bind(new IPEndPoint(bindAddress, 0));
                    }
                }
            }
            catch (SocketException ex)
            {
                ResetClientUnsafe();
                throw new UdpTrackerException($"DNS resolution failed for {uri.Host}: {ex.Message}", ex, isTransient: true);
            }
            catch
            {
                ResetClientUnsafe();
                throw;
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

        using var timeoutCts = new CancellationTokenSource(_requestTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var res = await _client.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);

                var expectedSender = _proxyUdpEndPoint ?? _endpoint;
                if (expectedSender == null || !NetworkUtils.NormalizeEndPoint(res.RemoteEndPoint).Equals(NetworkUtils.NormalizeEndPoint(expectedSender)))
                {
                    continue;
                }

                var buffer = res.Buffer;
                IPEndPoint? proxyRemote = null;
                if (_proxyUdpEndPoint != null)
                {
                    var (Payload, remote) = ProxyHelper.UnwrapSocks5UdpPacket(buffer);
                    if (Payload.IsEmpty || _endpoint == null || remote.Port != _endpoint.Port
                        || (_proxyTargetHost == null && !NetworkUtils.NormalizeEndPoint(remote).Equals(NetworkUtils.NormalizeEndPoint(_endpoint))))
                    {
                        continue;
                    }

                    buffer = Payload.ToArray();
                    proxyRemote = remote;
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

                        if (_proxyTargetHost != null && proxyRemote != null)
                        {
                            // Do not let a stale transaction select the resolved destination.
                            // A domain envelope cannot tell us which address family its peers use.
                            if (res.Buffer[3] == 0x03)
                            {
                                continue;
                            }
                            _endpoint = proxyRemote;
                            _proxyTargetHost = null;
                        }

                        return _proxyUdpEndPoint != null ? new UdpReceiveResult(buffer, res.RemoteEndPoint) : res;
                    }
                    // Else: Stale packet, ignore and loop
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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

        // BEP 41: the path and query of the tracker URL, appended as options after the fixed 98 byte
        // request. Without them a UDP announce is indistinguishable from one to any other endpoint on
        // the same host and port, which breaks trackers that authenticate on a passkey in the URL.
        // Non-supporting trackers read BEP 15's fixed offsets and ignore the trailing bytes, which is
        // exactly the extension point BEP 41 relies on ("parsing starts ... at byte offset 98").
        byte[] urlData = Torrent.Settings.SendUdpTrackerUrlData
            ? UdpTrackerUrlData.Encode(Url)
            : [];

        byte[] req = new byte[98 + urlData.Length];
        var span = req.AsSpan();
        urlData.CopyTo(span[98..]);

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
        int listenPort = Torrent.AdvertisedPeerPort;
        BinaryPrimitives.WriteUInt16BigEndian(span[96..], checked((ushort)listenPort));

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
            if (_proxyTargetHost != null)
            {
                headerLength = 7 + Encoding.ASCII.GetByteCount(_proxyTargetHost);
            }
            int totalLength = headerLength + data.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
            try
            {
                if (_proxyTargetHost != null)
                {
                    ProxyHelper.WriteSocks5UdpPacket(data.Span, _proxyTargetHost, _endpoint.Port, buffer.AsSpan(0, totalLength));
                }
                else
                {
                    ProxyHelper.WriteSocks5UdpPacket(data.Span, _endpoint, buffer.AsSpan(0, totalLength));
                }
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
