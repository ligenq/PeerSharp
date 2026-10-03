using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Implements NAT Port Mapping Protocol (NAT-PMP) - RFC 6886.
/// Simple binary UDP protocol for requesting port mappings from a gateway.
/// </summary>
internal class NatPmpPortMapping : IPortMapper
{
    private const int NatPmpPort = 5351;
    private readonly Func<IEnumerable<IPAddress>> _gatewayProvider;
    private readonly List<IPAddress> _gateways = [];
    private readonly ILogger<NatPmpPortMapping> _logger;
    private readonly List<(int Port, string Protocol)> _mappings = [];
    private readonly int _natPmpPort;
    private readonly Dictionary<IPAddress, (PortMappingResult MappingResult, string? Error, int? ExternalPort)> _status = [];
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(IPAddress Gateway, int Port, string Protocol), (int ExternalPort, uint Lifetime)> _leases = [];

    public TimeSpan RenewalInterval
    {
        get
        {
            lock (_status)
            {
                return _leases.Count == 0 ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromSeconds(Math.Min(300, _leases.Values.Min(lease => lease.Lifetime) / 2.0));
            }
        }
    }

    public NatPmpPortMapping()
        : this(GetDefaultGateways, NatPmpPort, TimeProvider.System, NullLoggerFactory.Instance)
    {
    }

    public NatPmpPortMapping(ILoggerFactory loggerFactory)
        : this(GetDefaultGateways, NatPmpPort, TimeProvider.System, loggerFactory)
    {
    }

    internal NatPmpPortMapping(Func<IEnumerable<IPAddress>> gatewayProvider, int natPmpPort)
        : this(gatewayProvider, natPmpPort, TimeProvider.System, NullLoggerFactory.Instance)
    {
    }

    internal NatPmpPortMapping(Func<IEnumerable<IPAddress>> gatewayProvider, int natPmpPort, TimeProvider timeProvider)
        : this(gatewayProvider, natPmpPort, timeProvider, NullLoggerFactory.Instance)
    {
    }

    internal NatPmpPortMapping(Func<IEnumerable<IPAddress>> gatewayProvider, int natPmpPort, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _gatewayProvider = gatewayProvider;
        _natPmpPort = natPmpPort;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<NatPmpPortMapping>();
    }

    public string Name => "NAT-PMP";

    public int? GetExternalPort(int internalPort, string protocol)
    {
        lock (_status)
        {
            return _leases.Where(entry => entry.Key.Port == internalPort && entry.Key.Protocol == protocol)
                .Select(entry => (int?)entry.Value.ExternalPort).FirstOrDefault();
        }
    }

    public IReadOnlyList<PortMappingStatus> GetStatus()
    {
        var result = new List<PortMappingStatus>();
        lock (_status)
        {
            if (_gateways.Count == 0)
            {
                result.Add(new PortMappingStatus(Name, PortMappingResult.Failed, null, "No gateways discovered"));
            }
            else
            {
                foreach (var kvp in _status)
                {
                    result.Add(new PortMappingStatus(
                        $"{Name} ({kvp.Key})",
                        kvp.Value.MappingResult,
                        kvp.Value.ExternalPort,
                        kvp.Value.Error));
                }
            }
        }
        return result;
    }

    public async Task<bool> MapPortAsync(int port, string protocol, string description, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        protocol = protocol.ToUpperInvariant();
        if (protocol is not ("TCP" or "UDP")) throw new ArgumentException("Expected TCP or UDP.", nameof(protocol));
        IPAddress[] gateways;
        lock (_status) gateways = [.. _gateways];
        if (gateways.Length == 0)
        {
            return false;
        }

        var results = await Task.WhenAll(gateways.Select(async gateway =>
        {
            int suggestedPort;
            lock (_status) suggestedPort = _leases.TryGetValue((gateway, port, protocol), out var lease) ? lease.ExternalPort : port;
            var result = await MapOnGatewayAsync(gateway, port, suggestedPort, protocol, _natPmpPort, _timeProvider, _logger, ct).ConfigureAwait(false);
            lock (_status)
            {
                if (result.Success) _leases[(gateway, port, protocol)] = (result.ExternalPort!.Value, result.Lifetime);
                else _leases.Remove((gateway, port, protocol));
                _status[gateway] = result.Success
                    ? (PortMappingResult.Success, null, result.ExternalPort)
                    : (PortMappingResult.Failed, "Mapping failed", null);
            }
            return result.Success;
        })).ConfigureAwait(false);
        bool anySuccess = results.Any(static success => success);

        if (anySuccess)
        {
            lock (_mappings)
            {
                if (!_mappings.Contains((port, protocol))) _mappings.Add((port, protocol));
            }
        }

        return anySuccess;
    }

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var gateways = _gatewayProvider().ToArray();
        lock (_status)
        {
            _gateways.Clear();
            _gateways.AddRange(gateways);
            _status.Clear();
            foreach (var key in _leases.Keys.Where(key => !gateways.Contains(key.Gateway)).ToArray()) _leases.Remove(key);
            foreach (var gateway in gateways) _status[gateway] = (PortMappingResult.Pending, null, null);
        }

        // NAT-PMP protocol dictates we should send requests to the default gateway
        foreach (var g in gateways)
        {
            _logger.LogInformation("NAT-PMP: Found gateway at {GatewayAddress}", g);
        }

        if (gateways.Length == 0)
        {
            _logger.LogInformation("NAT-PMP: No default gateways found");
        }
        else if (gateways.Length > 1)
        {
            _logger.LogWarning("NAT-PMP: Multiple gateways detected ({Count}). This may indicate a VPN or double-NAT configuration which can cause connectivity issues", gateways.Length);
        }

        return Task.CompletedTask;
    }

    public async Task UnmapAllAsync(CancellationToken ct)
    {
        List<(int Port, string Protocol)> toRemove;
        lock (_mappings)
        {
            toRemove = [.. _mappings];
            _mappings.Clear();
        }

        // Fan out across gateways - they are independent devices - but keep the ports for a
        // single gateway sequential. A flat mappings x gateways fan-out is exactly the burst
        // that consumer routers rate-limit or silently drop, turning a slow-but-reliable
        // teardown into a flaky one.
        IPAddress[] gateways;
        lock (_status)
        {
            gateways = [.. _gateways];
            _leases.Clear();
            foreach (var gateway in gateways) _status[gateway] = (PortMappingResult.NotAttempted, null, null);
        }
        await Task.WhenAll(gateways.Select(async gateway =>
        {
            foreach (var (port, protocol) in toRemove)
            {
                await UnmapOnGatewayAsync(gateway, port, protocol, _natPmpPort, _logger, ct).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    private static IEnumerable<IPAddress> GetDefaultGateways()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();
    }

    private static async Task<(bool Success, int? ExternalPort, uint Lifetime)> MapOnGatewayAsync(IPAddress gateway, int port, int suggestedPort, string protocol, int natPmpPort, TimeProvider timeProvider, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SendTimeout = 2000;
            client.Client.ReceiveTimeout = 2000;

            byte opCode = string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)2;

            // Request Packet:
            // Vers (1) | OP (1) | Reserved (2) | Internal Port (2) | External Port (2) | Lifetime (4)
            byte[] request = new byte[12];
            request[0] = 0; // Version 0
            request[1] = opCode;
            // Reserved 2-3 are 0
            request[4] = (byte)(port >> 8);
            request[5] = (byte)(port & 0xFF);
            request[6] = (byte)(suggestedPort >> 8);
            request[7] = (byte)(suggestedPort & 0xFF);
            // Lifetime: 3600 seconds (1 hour)
            request[8] = 0; request[9] = 0; request[10] = 0x0E; request[11] = 0x10;

            var endpoint = new IPEndPoint(gateway, natPmpPort);
            client.Connect(endpoint); // The socket rejects replies from other endpoints.
            var response = await SendMappingRequestAsync(client, request, timeProvider, ct).ConfigureAwait(false);
            if (response.RemoteEndPoint.Equals(endpoint) && response.Buffer.Length == 16 && response.Buffer[0] == 0 && response.Buffer[1] == (128 + opCode)
                && BinaryPrimitives.ReadUInt16BigEndian(response.Buffer.AsSpan(8)) == port)
            {
                int resultCode = (response.Buffer[2] << 8) | response.Buffer[3];
                if (resultCode == 0)
                {
                    int extPort = BinaryPrimitives.ReadUInt16BigEndian(response.Buffer.AsSpan(10));
                    uint lifetime = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(12));
                    if (extPort == 0 || lifetime == 0) return (false, null, 0);
                    logger.LogInformation("NAT-PMP: Mapped {Protocol} port {Internal}->{External} on {Gateway}", protocol, port, extPort, gateway);
                    return (true, extPort, lifetime);
                }
                logger.LogWarning("NAT-PMP: Gateway {Gateway} returned error code {Result}", gateway, resultCode);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // A gateway that does not answer is the ordinary case, not a fault: most consumer routers
            // speak UPnP and not NAT-PMP. Do not print the identical cancellation trace per gateway.
#pragma warning disable S6667
            logger.LogDebug(
                "NAT-PMP: no mapping from {Gateway} ({Reason}: {Message})",
                gateway,
                ex.GetType().Name,
                ex.Message);
#pragma warning restore S6667
        }
        catch (SocketException ex)
        {
            // An unreachable/refusing gateway is equally ordinary, but unexpected implementation or
            // configuration failures still fall through to the traced catch below.
#pragma warning disable S6667
            logger.LogDebug(
                "NAT-PMP: no mapping from {Gateway} ({Reason}: {Message})",
                gateway,
                ex.SocketErrorCode,
                ex.Message);
#pragma warning restore S6667
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "NAT-PMP: unexpected mapping failure on {Gateway}", gateway);
        }
        return (false, null, 0);
    }

    private static async Task<UdpReceiveResult> SendMappingRequestAsync(UdpClient client, byte[] request, TimeProvider timeProvider, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), timeProvider);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await client.SendAsync(request, scope.Token).ConfigureAwait(false);
            using var retry = new CancellationTokenSource(TimeSpan.FromMilliseconds(250 * (1 << attempt)), timeProvider);
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, retry.Token);
            try { return await client.ReceiveAsync(receive.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!scope.IsCancellationRequested) { /* Retry a lost datagram with exponential backoff. */ }
        }
        throw new OperationCanceledException(scope.Token);
    }

    private static async Task UnmapOnGatewayAsync(IPAddress gateway, int port, string protocol, int natPmpPort, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            byte opCode = string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)2;

            // To unmap, send request with lifetime 0
            byte[] request = new byte[12];
            request[0] = 0;
            request[1] = opCode;
            request[4] = (byte)(port >> 8);
            request[5] = (byte)(port & 0xFF);
            // External port 0 and Lifetime 0

            await client.SendAsync(request, new IPEndPoint(gateway, natPmpPort), ct).ConfigureAwait(false);
            logger.LogInformation("NAT-PMP: Unmapped {Protocol} port {Port} on {Gateway}", protocol, port, gateway);
        }
        catch { /* Best effort on shutdown */ }
    }
}
