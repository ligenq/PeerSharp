using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Implements NAT Port Mapping Protocol (NAT-PMP) - RFC 6886.
/// Simple binary UDP protocol for requesting port mappings from a gateway.
/// </summary>
internal class NatPmpPortMapping : IPortMapper
{
    private const int NatPmpPort = 5351;
    private static readonly ILogger _logger = TorrentLoggerFactory.CreateLogger<NatPmpPortMapping>();
    private readonly Func<IEnumerable<IPAddress>> _gatewayProvider;
    private readonly List<IPAddress> _gateways = new();
    private readonly List<(int Port, string Protocol)> _mappings = new();
    private readonly int _natPmpPort;
    private readonly Dictionary<IPAddress, (PortMappingResult MappingResult, string? Error, int? ExternalPort)> _status = new();
    private readonly TimeProvider _timeProvider;

    public NatPmpPortMapping()
        : this(GetDefaultGateways, NatPmpPort, TimeProvider.System)
    {
    }

    internal NatPmpPortMapping(Func<IEnumerable<IPAddress>> gatewayProvider, int natPmpPort)
        : this(gatewayProvider, natPmpPort, TimeProvider.System)
    {
    }

    internal NatPmpPortMapping(Func<IEnumerable<IPAddress>> gatewayProvider, int natPmpPort, TimeProvider timeProvider)
    {
        _gatewayProvider = gatewayProvider;
        _natPmpPort = natPmpPort;
        _timeProvider = timeProvider;
    }

    public string Name => "NAT-PMP";

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
        if (_gateways.Count == 0)
        {
            return false;
        }

        bool anySuccess = false;
        foreach (var gateway in _gateways)
        {
            var result = await MapOnGatewayAsync(gateway, port, protocol, _natPmpPort, _timeProvider, ct).ConfigureAwait(false);
            if (result.Success)
            {
                anySuccess = true;
                lock (_status)
                {
                    _status[gateway] = (PortMappingResult.Success, null, result.ExternalPort);
                }
            }
            else
            {
                lock (_status)
                {
                    _status[gateway] = (PortMappingResult.Failed, "Mapping failed", null);
                }
            }
        }

        if (anySuccess)
        {
            lock (_mappings)
            {
                _mappings.Add((port, protocol));
            }
        }

        return anySuccess;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        _gateways.Clear();
        lock (_status)
        {
            _status.Clear();
        }

        // NAT-PMP protocol dictates we should send requests to the default gateway
        var gateways = _gatewayProvider();

        foreach (var g in gateways)
        {
            _gateways.Add(g);
            lock (_status)
            {
                _status[g] = (PortMappingResult.Pending, null, null);
            }

            _logger.LogInformation("NAT-PMP: Found gateway at {GatewayAddress}", g);
        }

        if (_gateways.Count == 0)
        {
            _logger.LogInformation("NAT-PMP: No default gateways found");
        }
        else if (_gateways.Count > 1)
        {
            _logger.LogWarning("NAT-PMP: Multiple gateways detected ({Count}). This may indicate a VPN or double-NAT configuration which can cause connectivity issues", _gateways.Count);
        }
    }

    public async Task UnmapAllAsync(CancellationToken ct)
    {
        if (_gateways.Count == 0)
        {
            return;
        }

        List<(int Port, string Protocol)> toRemove;
        lock (_mappings)
        {
            toRemove = _mappings.ToList();
            _mappings.Clear();
        }

        foreach (var (port, protocol) in toRemove)
        {
            foreach (var gateway in _gateways)
            {
                await UnmapOnGatewayAsync(gateway, port, protocol, _natPmpPort, ct).ConfigureAwait(false);
            }
        }
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

    private static async Task<(bool Success, int? ExternalPort)> MapOnGatewayAsync(IPAddress gateway, int port, string protocol, int natPmpPort, TimeProvider timeProvider, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SendTimeout = 2000;
            client.Client.ReceiveTimeout = 2000;

            byte opCode = string.Equals(protocol, "UDP", StringComparison.CurrentCultureIgnoreCase) ? (byte)1 : (byte)2;

            // Request Packet:
            // Vers (1) | OP (1) | Reserved (2) | Internal Port (2) | External Port (2) | Lifetime (4)
            byte[] request = new byte[12];
            request[0] = 0; // Version 0
            request[1] = opCode;
            // Reserved 2-3 are 0
            request[4] = (byte)(port >> 8);
            request[5] = (byte)(port & 0xFF);
            request[6] = (byte)(port >> 8);
            request[7] = (byte)(port & 0xFF);
            // Lifetime: 3600 seconds (1 hour)
            request[8] = 0; request[9] = 0; request[10] = 0x0E; request[11] = 0x10;

            var endpoint = new IPEndPoint(gateway, natPmpPort);
            await client.SendAsync(request, endpoint, ct).ConfigureAwait(false);

            var receiveTask = client.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2), timeProvider, ct)).ConfigureAwait(false) == receiveTask)
            {
                var response = await receiveTask.ConfigureAwait(false);
                if (response.Buffer.Length >= 12 && response.Buffer[0] == 0 && response.Buffer[1] == (128 + opCode))
                {
                    int resultCode = (response.Buffer[2] << 8) | response.Buffer[3];
                    if (resultCode == 0)
                    {
                        int extPort = (response.Buffer[8] << 8) | response.Buffer[9];
                        _logger.LogInformation("NAT-PMP: Mapped {Protocol} port {Internal}->{External} on {Gateway}", protocol, port, extPort, gateway);
                        return (true, extPort);
                    }
                    _logger.LogWarning("NAT-PMP: Gateway {Gateway} returned error code {Result}", gateway, resultCode);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "NAT-PMP: Error mapping on {Gateway}", gateway);
            }
        }
        return (false, null);
    }

    private static async Task UnmapOnGatewayAsync(IPAddress gateway, int port, string protocol, int natPmpPort, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            byte opCode = string.Equals(protocol, "UDP", StringComparison.CurrentCultureIgnoreCase) ? (byte)1 : (byte)2;

            // To unmap, send request with lifetime 0
            byte[] request = new byte[12];
            request[0] = 0;
            request[1] = opCode;
            request[4] = (byte)(port >> 8);
            request[5] = (byte)(port & 0xFF);
            // External port 0 and Lifetime 0

            await client.SendAsync(request, new IPEndPoint(gateway, natPmpPort), ct).ConfigureAwait(false);
            _logger.LogInformation("NAT-PMP: Unmapped {Protocol} port {Port} on {Gateway}", protocol, port, gateway);
        }
        catch { /* Best effort on shutdown */ }
    }
}
