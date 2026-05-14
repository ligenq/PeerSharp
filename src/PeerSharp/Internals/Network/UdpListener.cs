using Microsoft.Extensions.Logging;
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
    private readonly Lock _lock = new();
    private readonly ILogger<UdpListener> _logger = TorrentLoggerFactory.CreateLogger<UdpListener>();
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

    public UdpListener(int port, IUdpSocketFactory socketFactory, Settings settings)
    {
        _port = port;
        _socketFactory = socketFactory;
        _settings = settings;
    }

    public int Port => _client?.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : _port;

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
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

    public async Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
    {
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
                await _client.SendAsync(data, endpoint, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var proxy = _settings.Proxy;
        if (proxy.Type == ProxyType.Socks5 && !string.IsNullOrEmpty(proxy.Host))
        {
            _logger.LogInformation("Starting UDP listener via SOCKS5 proxy {ProxyHost}:{ProxyPort}", proxy.Host, proxy.Port);
            try
            {
                var result = await ProxyHelper.ConnectSocks5UdpAsync(proxy.Host, proxy.Port, proxy.Username, proxy.Password, _cts.Token).ConfigureAwait(false);
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
            _client = _socketFactory.Create(_port);
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

    public void Stop()
    {
        StopInternal();

        // Wait for processing task to complete synchronously
        try
        {
            _processTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            _logger.LogTrace(ex, "UdpListener process task exception during stop (likely cancelled)");
        }

        // Wait for receive task to complete synchronously
        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            _logger.LogTrace(ex, "UdpListener receive task exception during stop (likely cancelled)");
        }

        CleanupResources();
    }

    public async Task StopAsync()
    {
        StopInternal();

        // Wait for processing task to complete asynchronously
        if (_processTask != null)
        {
            try
            {
                await _processTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogTrace(ex, "UdpListener process task timed out during async stop");
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "UdpListener process task exception during async stop");
            }
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogTrace(ex, "UdpListener receive task timed out during async stop");
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "UdpListener receive task exception during async stop");
            }
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

        while (_running && _client != null && !token.IsCancellationRequested)
        {
            try
            {
                var result = await _client.ReceiveAsync(token).ConfigureAwait(false);
                var data = result.Buffer;
                var remoteEndPoint = result.RemoteEndPoint;

                if (_proxyUdpEndPoint != null)
                {
                    var unwrapped = ProxyHelper.UnwrapSocks5UdpPacket(data);
                    if (unwrapped.Payload.IsEmpty)
                    {
                        continue;
                    }

                    data = unwrapped.Payload.ToArray();
                    remoteEndPoint = unwrapped.RemoteEndPoint;
                }

                if (_receiveChannel != null)
                {
                    await _receiveChannel.Writer.WriteAsync((data, remoteEndPoint)).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown - socket was disposed
                break;
            }
            catch (SocketException ex)
            {
                // Network errors are expected (e.g., ICMP port unreachable, connection reset)
                if (_running)
                {
                    _logger.LogWarning(ex, "UDP receive socket error: {SocketErrorCode} - {Message}", ex.SocketErrorCode, ex.Message);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _logger.LogError(ex, "UDP receive unexpected error");
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
