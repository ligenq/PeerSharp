using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Grouped network services to avoid constructor parameter explosion.
/// </summary>
internal sealed record NetworkServices(
    IDhtManager Dht,
    IUtpManager Utp,
    IPortListener PortListener,
    IUdpListener UdpListener,
    ILsdManager Lsd,
    IPortMapperFactory PortMapperFactory);

internal class NetworkManager : INetworkManager
{
    private static readonly TimeSpan PortUnmapTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<NetworkManager> _logger;
    private readonly Action<UtpStream> _onUtpConnection;
    private readonly List<IPortMapper> _portMappers = [];
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private bool _stopped;
    private readonly NetworkServices _services;
    private readonly Settings _settings;
    private AtomicDisposal _disposal = new();
    private CancellationTokenSource? _portMappingCts;
    private Task? _portMappingTask;

    public NetworkManager(
        Settings settings,
        Action<UtpStream> onUtpConnection,
        NetworkServices services)
        : this(settings, onUtpConnection, services, NullLoggerFactory.Instance)
    {
    }

    public NetworkManager(
        Settings settings,
        Action<UtpStream> onUtpConnection,
        NetworkServices services,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NetworkManager>();
        _settings = settings;
        _onUtpConnection = onUtpConnection;
        _services = services;
        Blocklist = new IpBlocklist(loggerFactory);
    }

    public IpBlocklist Blocklist { get; }
    public int BoundTcpPort => PortListener.Port;
    public int BoundUdpPort => UdpListener.Port;
    public IDhtManager Dht => _services.Dht;
    public ILsdManager Lsd => _services.Lsd;
    public IPortListener PortListener => _services.PortListener;
    public IUtpManager Utp => _services.Utp;
    private IUdpListener UdpListener => _services.UdpListener;

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<PortMappingStatus> GetPortMappingStatus()
    {
        var result = new List<PortMappingStatus>();
        foreach (var mapper in _portMappers)
        {
            result.AddRange(mapper.GetStatus());
        }
        return result.AsReadOnly();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings;
        bool udpEnabled = settings.Connection.EnableUtpIn
            || settings.Connection.EnableUtpOut
            || settings.Dht.Enabled
            || settings.Connection.EnableLsd;

        // Initialize packet handlers
        if (settings.Connection.EnableUtpIn || settings.Connection.EnableUtpOut)
        {
            Utp.OnNewConnection = _onUtpConnection;
            Utp.Start(UdpListener);
        }

        if (settings.Dht.Enabled)
        {
            await Dht.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.Connection.EnableLsd)
        {
            Lsd.Start();
        }

        // Start receiving packets
        if (udpEnabled)
        {
            await UdpListener.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // TCP listener
        if (settings.Connection.EnableTcpIn)
        {
            PortListener.Start(settings.Connection.TcpPort);
        }

        _portMappers.AddRange(_services.PortMapperFactory.CreateMappers(settings));

        if (_portMappers.Count > 0)
        {
            if (_portMappingCts != null)
            {
                await _portMappingCts.CancelAsync().ConfigureAwait(false);
                _portMappingCts.Dispose();
            }

            _portMappingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _portMappingCts.CancelAfter(TimeSpan.FromSeconds(10));

            _portMappingTask = StartPortMappingSafeAsync(BoundTcpPort, udpEnabled ? BoundUdpPort : 0, _portMappingCts.Token);
            _ = _portMappingTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogWarning(t.Exception?.GetBaseException(), "StartPortMappingSafeAsync failed");
                }
            }, TaskScheduler.Default);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _stopLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            await StopCoreAsync(ct).ConfigureAwait(false);
            _stopped = true;
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PortListener.Stop();
        _logger.LogDebug("TCP listener shutdown completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();
        await Dht.StopAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("DHT shutdown completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();
        Lsd.Stop();
        _logger.LogDebug("LSD shutdown completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();
        Utp.Stop();
        _logger.LogDebug("uTP shutdown completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();
        await UdpListener.StopAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("UDP listener shutdown completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);

        stopwatch.Restart();
        if (_portMappingCts != null)
        {
            await _portMappingCts.CancelAsync().ConfigureAwait(false);
        }
        if (_portMappingTask != null)
        {
            try
            {
                await _portMappingTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogTrace(ex, "Port mapping did not finish before shutdown");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Port mapping task ended with error during shutdown");
            }
        }
        _portMappingCts?.Dispose();
        _portMappingCts = null;
        _portMappingTask = null;
        _logger.LogDebug("Network shutdown mapping task completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);

        stopwatch.Restart();
        if (_portMappers.Count > 0)
        {
            // Router cleanup is best-effort. A NAT-PMP/UPnP gateway that does not
            // answer must not hold desktop application shutdown for several seconds.
            using var timeoutCts = new CancellationTokenSource(PortUnmapTimeout);
            using var unmapCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await UnmapPortsSafeAsync(unmapCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UnmapPortsSafeAsync failed");
            }
        }
        _logger.LogDebug("Network shutdown port unmapping completed in {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
    }

    private async Task StartPortMappingSafeAsync(int tcpPort, int udpPort, CancellationToken ct)
    {
        foreach (var mapper in _portMappers)
        {
            try
            {
                await mapper.StartAsync(ct).ConfigureAwait(false);
                await mapper.MapPortAsync(tcpPort, "TCP", "PeerSharp TCP", ct).ConfigureAwait(false);
                if (udpPort <= 0)
                {
                    continue;
                }

                if (udpPort != tcpPort)
                {
                    await mapper.MapPortAsync(udpPort, "UDP", "PeerSharp UDP", ct).ConfigureAwait(false);
                }
                else
                {
                    await mapper.MapPortAsync(tcpPort, "UDP", "PeerSharp UDP", ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Non-critical mapping failure
            }
        }
    }

    private async Task UnmapPortsSafeAsync(CancellationToken ct)
    {
        foreach (var mapper in _portMappers)
        {
            try
            {
                await mapper.UnmapAllAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Non-critical unmapping failure
            }
        }
    }
}
