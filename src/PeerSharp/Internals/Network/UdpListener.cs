using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace PeerSharp.Internals.Network;

internal interface IUdpReceiver
{
    void Receive(byte[] data, IPEndPoint remote);
}

internal class UdpListener : IUdpListener
{
    /// <summary>
    /// How long a destination is left alone after a send finds no route to it. Other addresses in
    /// its family may still be reachable, including LAN peers when there is no route to the internet.
    /// </summary>
    internal static readonly TimeSpan UnroutableAddressBackoff = TimeSpan.FromMinutes(1);

    internal const int MaxUnroutableAddresses = 256;

    private readonly Lock _lock = new();
    private readonly ILogger<UdpListener> _logger;
    private readonly int _port;
    private readonly Settings _settings;
    private readonly IUdpSocketFactory _socketFactory;
    private IUdpSocket? _client;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private Task? _processTask;
    private TcpClient? _proxyControlClient;
    private IPEndPoint? _proxyUdpEndPoint;
    private Channel<(byte[] Data, IPEndPoint Remote)>? _receiveChannel;
    private IUdpReceiver[] _receivers = [];
    private Task? _receiveTask;
    private bool _running;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<IPAddress, DateTimeOffset> _unroutableAddresses = [];

    // Mirrors _unroutableAddresses.Count, readable without the lock. Every datagram - each uTP data
    // packet among them - asks whether its destination is unroutable, and the answer is almost always
    // that nothing is, so that case must not queue on a lock shared with the rest of the listener.
    private int _unroutableCount;

    public UdpListener(int port, IUdpSocketFactory socketFactory, Settings settings)
        : this(port, socketFactory, settings, NullLoggerFactory.Instance)
    {
    }

    public UdpListener(int port, IUdpSocketFactory socketFactory, Settings settings, ILoggerFactory loggerFactory)
        : this(port, socketFactory, settings, loggerFactory, TimeProvider.System)
    {
    }

    public UdpListener(int port, IUdpSocketFactory socketFactory, Settings settings, ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        _port = port;
        _socketFactory = socketFactory;
        _settings = settings;
        _logger = loggerFactory.CreateLogger<UdpListener>();
        _timeProvider = timeProvider;
    }

    public int Port => _client?.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : _port;

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public void RegisterReceiver(IUdpReceiver receiver)
    {
        lock (_lock)
        {
            var newReceivers = new IUdpReceiver[_receivers.Length + 1];
            Array.Copy(_receivers, newReceivers, _receivers.Length);
            newReceivers[^1] = receiver;
            _receivers = newReceivers;
        }
    }

    /// <summary>
    /// Sends one datagram. Like the network underneath it, this does not promise delivery: a datagram
    /// to an address nothing can be sent to, or a destination with no route, is dropped without
    /// throwing, which is what every caller did with the exception anyway.
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
    {
        // Peers and DHT nodes hand out addresses such as 0.0.0.0 and the reserved 240/4 block. The
        // socket refuses those with an exception, so they are dropped before it sees them.
        if (!NetworkUtils.IsDeliverableUnicast(endpoint))
        {
            return;
        }

        if (_client != null)
        {
            if (_proxyUdpEndPoint != null)
            {
                int headerLength = endpoint.AddressFamily == AddressFamily.InterNetwork ? 10 : 22;
                int totalLength = headerLength + data.Length;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
                try
                {
                    ProxyHelper.WriteSocks5UdpPacket(data.Span, endpoint, buffer.AsSpan(0, totalLength));
                    await _client.SendAsync(buffer.AsMemory(0, totalLength), _proxyUdpEndPoint, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
                if (IsUnroutable(address))
                {
                    return;
                }

                try
                {
                    await _client.SendAsync(data, endpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkUnreachable)
                {
                    MarkUnroutable(address);
                    _logger.LogDebug(ex, "No route to {Address}; not sending to it for {Backoff}", address, UnroutableAddressBackoff);
                }
            }
        }
    }

    private bool IsUnroutable(IPAddress address)
    {
        if (Volatile.Read(ref _unroutableCount) == 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_unroutableAddresses.TryGetValue(address, out var until))
            {
                return false;
            }

            if (_timeProvider.GetUtcNow() < until)
            {
                return true;
            }

            _unroutableAddresses.Remove(address);
            Volatile.Write(ref _unroutableCount, _unroutableAddresses.Count);
            return false;
        }
    }

    private void MarkUnroutable(IPAddress address)
    {
        lock (_lock)
        {
            // Discovery supplies untrusted destinations. Bound their retained state, evicting the
            // oldest deadline first (which also prefers expired entries).
            if (_unroutableAddresses.Count >= MaxUnroutableAddresses && !_unroutableAddresses.ContainsKey(address))
            {
                _unroutableAddresses.Remove(_unroutableAddresses.MinBy(entry => entry.Value).Key);
            }

            _unroutableAddresses[address] = _timeProvider.GetUtcNow() + UnroutableAddressBackoff;
            Volatile.Write(ref _unroutableCount, _unroutableAddresses.Count);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            return;
        }

        var proxy = _settings.Proxy;
        bool utpEnabled = _settings.Connection.EnableUtpIn || _settings.Connection.EnableUtpOut;
        bool proxySharedUdp = _settings.Dht.Enabled || (utpEnabled && proxy.ProxyPeers);
        var decision = UdpProxyPolicy.Decide(proxy, proxySharedUdp);

        if (decision == UdpProxyPolicy.Decision.Refuse)
        {
            // Only SOCKS5 can tunnel UDP. Binding a direct socket here would put the real address in
            // front of every DHT node while the traffic the proxy was configured for goes through it.
            throw new InvalidOperationException(
                $"A {proxy.Type} proxy is configured, which cannot carry UDP. Refusing to send DHT and " +
                "uTP traffic directly, because that would expose the address the proxy exists to hide. " +
                "Use a SOCKS5 proxy, or turn off DHT and uTP.");
        }

        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (decision == UdpProxyPolicy.Decision.TunnelThroughSocks5)
        {
            _logger.LogInformation("Starting UDP listener via SOCKS5 proxy {ProxyHost}:{ProxyPort}", proxy.Host, proxy.Port);
            try
            {
                var result = await ProxyHelper.ConnectSocks5UdpAsync(
                    proxy.Host,
                    proxy.Port,
                    proxy.Username,
                    proxy.Password,
                    _logger,
                    _settings.Connection.BindAddress,
                    _cts.Token).ConfigureAwait(false);
                _client = new UdpSocketAdapter(result.UdpClient, true);
                _proxyUdpEndPoint = result.ProxyUdpEndPoint;
                _proxyControlClient = result.ControlClient;
            }
            catch (Exception)
            {
                _running = false;
                throw;
            }
        }
        else
        {
            var bindAddress = _settings.Connection.BindAddress;
            _client = ListenPortBinder.Bind(_port, port => CreateBoundSocket(bindAddress, port), _logger, "UDP");
            _logger.LogInformation("UDP listener bound to {LocalEndPoint}", _client.Client.LocalEndPoint);
        }

        // Bounded channel to prevent memory exhaustion during UDP flood.
        _receiveChannel = Channel.CreateBounded<(byte[] Data, IPEndPoint Remote)>(
            new BoundedChannelOptions(2000)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });

        _processTask = ProcessLoopAsync(_cts.Token);
        _receiveTask = ReceiveLoopAsync();
    }

    /// <summary>
    /// Creates a socket bound to one candidate port, leaking nothing if the bind fails.
    /// </summary>
    private IUdpSocket CreateBoundSocket(IPAddress? bindAddress, int port)
    {
        if (bindAddress == null)
        {
            return _socketFactory.Create(port);
        }

        var client = _socketFactory.Create(bindAddress.AddressFamily);
        try
        {
            client.Client.Bind(new IPEndPoint(bindAddress, port));
            return client;
        }
        catch
        {
            // The binder retries on the next port, so this socket must not outlive the attempt.
            client.Dispose();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopInternal();

        Task[] tasks = [_processTask ?? Task.CompletedTask, _receiveTask ?? Task.CompletedTask];
        try
        {
            // Cancellation plus closing the socket normally completes both loops immediately.
            // Keep a small shared grace period for faulty/non-cooperative socket adapters, rather
            // than spending up to two seconds on each task during application shutdown.
            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "UDP listener tasks did not finish within the shutdown grace period");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "UDP listener task exception during async stop");
        }

        CleanupResources();
    }

    private void CleanupResources()
    {
        _processTask = null;
        _receiveTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void Dispatch(byte[] data, IPEndPoint remote)
    {
        var receivers = _receivers;
        foreach (var receiver in receivers)
        {
            try
            {
                receiver.Receive(data, remote);
            }
            catch (Exception ex)
            {
                // Log but continue - one receiver failure shouldn't crash the loop
                _logger.LogError(ex, "UDP receiver error from {Remote}", remote);
            }
        }
    }

    private async Task ProcessLoopAsync(CancellationToken token)
    {
        try
        {
            if (_receiveChannel == null)
            {
                return;
            }

            while (await _receiveChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_receiveChannel.Reader.TryRead(out var item))
                {
                    Dispatch(item.Data, item.Remote);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (ChannelClosedException)
        {
            // Expected when channel is completed during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP process loop error");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        // Fallback to non-cancelable if listener isn't started yet.
        var token = _cts?.Token ?? CancellationToken.None;
        int consecutiveReceiveErrors = 0;

        while (_running && _client != null && !token.IsCancellationRequested)
        {
            try
            {
                var result = await _client.ReceiveAsync(token).ConfigureAwait(false);
                consecutiveReceiveErrors = 0;
                var data = result.Buffer;
                var remoteEndPoint = result.RemoteEndPoint;

                if (_proxyUdpEndPoint != null)
                {
                    var (Payload, RemoteEndPoint) = ProxyHelper.UnwrapSocks5UdpPacket(data);
                    if (Payload.IsEmpty)
                    {
                        continue;
                    }

                    data = Payload.ToArray();
                    remoteEndPoint = RemoteEndPoint;
                }

                if (_receiveChannel != null)
                {
                    await _receiveChannel.Writer.WriteAsync((data, remoteEndPoint), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown - socket was disposed
                break;
            }
            catch (OperationCanceledException)
            {
                // Listener stopping
                break;
            }
            catch (SocketException ex)
            {
                // Network errors are expected (e.g., ICMP port unreachable, connection reset)
                if (_running)
                {
                    _logger.LogWarning(ex, "UDP receive socket error: {SocketErrorCode} - {Message}", ex.SocketErrorCode, ex.Message);
                }
                consecutiveReceiveErrors++;
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _logger.LogError(ex, "UDP receive unexpected error");
                }
                consecutiveReceiveErrors++;
            }

            // A persistently failing socket must not hot-spin the loop (and flood the log).
            // Single transient errors (ICMP resets) keep full speed; repeated failures back off.
            int delayMs = UdpReceiveBackoff.ComputeDelayMs(consecutiveReceiveErrors);
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void StopInternal()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _cts?.Cancel();
        _client?.Close();
        _client = null;
        _proxyControlClient?.Dispose();
        _proxyControlClient = null;
        _proxyUdpEndPoint = null;
        _receiveChannel?.Writer.TryComplete();
    }
}
