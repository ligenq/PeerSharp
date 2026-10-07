using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utp;
using System.Net.NetworkInformation;
using System.Threading.Channels;

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
    private readonly TimeProvider _timeProvider;
    private readonly Action? _onAdvertisedPortChanged;

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
        : this(settings, onUtpConnection, services, loggerFactory, TimeProvider.System)
    {
    }

    public NetworkManager(
        Settings settings,
        Action<UtpStream> onUtpConnection,
        NetworkServices services,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        Action? onAdvertisedPortChanged = null)
    {
        _logger = loggerFactory.CreateLogger<NetworkManager>();
        _settings = settings;
        _onUtpConnection = onUtpConnection;
        _services = services;
        _timeProvider = timeProvider;
        _onAdvertisedPortChanged = onAdvertisedPortChanged;
        Blocklist = new IpBlocklist(loggerFactory);
    }

    public IpBlocklist Blocklist { get; }
    public int BoundTcpPort => PortListener.Port;
    public int BoundUdpPort => UdpListener.Port;
    public int AdvertisedPeerPort
    {
        get
        {
            bool tcp = _settings.Connection.EnableTcpIn && BoundTcpPort > 0;
            int localPort = tcp ? BoundTcpPort : BoundUdpPort;
            if (localPort <= 0) return _settings.Connection.TcpPort;
            return _portMappers.Select(mapper => mapper.GetExternalPort(localPort, tcp ? "TCP" : "UDP"))
                .FirstOrDefault(port => port.HasValue) ?? localPort;
        }
    }
    public IDhtManager Dht => _services.Dht;
    public ILsdManager Lsd => _services.Lsd;
    public IPortListener PortListener => _services.PortListener;
    public IUtpManager Utp => _services.Utp;
    private IUdpListener UdpListener => _services.UdpListener;

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
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
        // StopAsync marks the manager stopped so repeated stops are cheap. Initialization can be
        // retried after a later phase fails, so a new start must make the manager stoppable again.
        _stopped = false;

        var settings = _settings;
        bool sharedUdpEnabled = settings.Connection.EnableUtpIn
            || settings.Connection.EnableUtpOut
            || settings.Dht.Enabled;

        // Initialize packet handlers
        if (settings.Connection.EnableUtpIn || settings.Connection.EnableUtpOut)
        {
            Utp.OnNewConnection = settings.Connection.EnableUtpIn ? _onUtpConnection : null;
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
        if (sharedUdpEnabled)
        {
            await UdpListener.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // TCP listener
        if (settings.Connection.EnableTcpIn)
        {
            PortListener.Start(settings.Connection.TcpPort);
        }

        // Reuse the mapper instances after an initialization rollback. Adding another factory
        // batch on every retry would duplicate every map and unmap request.
        if (_portMappers.Count == 0)
        {
            _portMappers.AddRange(_services.PortMapperFactory.CreateMappers(settings));
        }

        if (_portMappers.Count > 0)
        {
            if (_portMappingCts != null)
            {
                await _portMappingCts.CancelAsync().ConfigureAwait(false);
                if (_portMappingTask != null) await _portMappingTask.ConfigureAwait(false);
                _portMappingCts.Dispose();
            }

            _portMappingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _portMappingTask = MaintainPortMappingsAsync(settings.Connection.EnableTcpIn ? BoundTcpPort : 0,
                sharedUdpEnabled ? BoundUdpPort : 0, _portMappingCts.Token);
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

    private async Task MaintainPortMappingsAsync(int tcpPort, int udpPort, CancellationToken ct)
    {
        var changes = Channel.CreateBounded<bool>(1);
        void OnAddressChanged(object? sender, EventArgs args) => changes.Writer.TryWrite(true);
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        int advertisedPort = AdvertisedPeerPort;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAll(_portMappers.Select(async mapper =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), _timeProvider);
                    using var round = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    try
                    {
                        await mapper.StartAsync(round.Token).ConfigureAwait(false);
                        if (tcpPort > 0) await mapper.MapPortAsync(tcpPort, "TCP", "PeerSharp TCP", round.Token).ConfigureAwait(false);
                        if (udpPort > 0) await mapper.MapPortAsync(udpPort, "UDP", "PeerSharp UDP", round.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (round.IsCancellationRequested) { /* Bounded refresh or shutdown. */ }
                    catch (Exception ex) { _logger.LogDebug(ex, "Port mapping refresh failed for {Mapper}", mapper.Name); }
                })).ConfigureAwait(false);

                int currentPort = AdvertisedPeerPort;
                if (!ct.IsCancellationRequested && currentPort != advertisedPort)
                {
                    advertisedPort = currentPort;
                    try { _onAdvertisedPortChanged?.Invoke(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not announce changed external port"); }
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(_portMappers.Min(mapper => mapper.RenewalInterval), _timeProvider, wait.Token);
                var changed = changes.Reader.WaitToReadAsync(wait.Token).AsTask();
                await Task.WhenAny(delay, changed).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                while (changes.Reader.TryRead(out _)) { /* Coalesce address changes. */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Normal shutdown. */ }
        finally { NetworkChange.NetworkAddressChanged -= OnAddressChanged; }
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
