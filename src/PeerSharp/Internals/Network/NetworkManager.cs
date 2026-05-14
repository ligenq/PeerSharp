using Microsoft.Extensions.Logging;
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
    private readonly ILogger<NetworkManager> _logger = TorrentLoggerFactory.CreateLogger<NetworkManager>();
    private readonly Action<UtpStream> _onUtpConnection;
    private readonly List<IPortMapper> _portMappers = [];
    private readonly NetworkServices _services;
    private readonly Settings _settings;
    private AtomicDisposal _disposal = new();
    private CancellationTokenSource? _portMappingCts;
    private Task? _portMappingTask;

    public NetworkManager(
        Settings settings,
        Action<UtpStream> onUtpConnection,
        NetworkServices services)
    {
        _settings = settings;
        _onUtpConnection = onUtpConnection;
        _services = services;
    }

    public IpBlocklist Blocklist { get; } = new();
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
        PortListener.Stop();
        await Dht.StopAsync(ct).ConfigureAwait(false);
        Lsd.Stop();
        Utp.Stop();
        await UdpListener.StopAsync().ConfigureAwait(false);

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

        if (_portMappers.Count > 0)
        {
            using var unmapCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await UnmapPortsSafeAsync(unmapCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UnmapPortsSafeAsync failed");
            }
        }
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
            catch
            {
                // Non-critical unmapping failure
            }
        }
    }
}
