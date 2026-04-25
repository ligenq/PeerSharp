using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace PeerSharp.Internals.Utilities;

internal static class UpnpDiscovery
{
    private const string SsdpIp = "239.255.255.250";

    private const string SsdpMessage =
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 3\r\n\r\n";

    private const int SsdpPort = 1900;
    private static readonly ILogger _logger = TorrentLoggerFactory.CreateLogger(nameof(UpnpDiscovery));

    public static Task<List<UpnpGateway>> DiscoverAsync(CancellationToken ct = default)
    {
        return DiscoverAsync(GetLocalIPs, new IPEndPoint(IPAddress.Parse(SsdpIp), SsdpPort), ParseDescriptionAsync, TimeProvider.System, ct);
    }

    internal static async Task<List<UpnpGateway>> DiscoverAsync(
        Func<IEnumerable<IPAddress>> localIpProvider,
        IPEndPoint ssdpEndpoint,
        Func<string, IPAddress, CancellationToken, Task<UpnpGateway?>> parseDescriptionAsync,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        timeProvider ??= TimeProvider.System;
        var gateways = new List<UpnpGateway>();
        var clients = new List<UdpClient>();
        var tasks = new List<Task>();

        try
        {
            foreach (var ip in localIpProvider())
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(ip, 0))
                    {
                        EnableBroadcast = true
                    };
                    clients.Add(client);

                    tasks.Add(ReceiveLoopAsync(client, gateways, ip, parseDescriptionAsync, ct));

                    // Send M-SEARCH
                    var data = Encoding.ASCII.GetBytes(SsdpMessage);

                    for (int i = 0; i < 2; i++)
                    {
                        await client.SendAsync(data, ssdpEndpoint, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bind error on {Ip}", ip);
                }
            }

            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(3), timeProvider, ct)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in clients)
            {
                c.Close();
            }
        }

        return gateways;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint", "S1075:URIs should not be hardcoded", Justification = "URL separator, not path delimiter")]
    internal static async Task<UpnpGateway?> ParseDescriptionAsync(string location, IPAddress localIp, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(2);
            var xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device == null)
            {
                return null;
            }

            var friendlyName = device.Element(ns + "friendlyName")?.Value ?? "Unknown";
            var serviceList = doc.Descendants(ns + "serviceList").FirstOrDefault();
            if (serviceList == null)
            {
                return null;
            }

            foreach (var service in serviceList.Elements(ns + "service"))
            {
                var serviceType = service.Element(ns + "serviceType")?.Value;
                var controlUrl = service.Element(ns + "controlURL")?.Value;

                if (serviceType != null && controlUrl != null &&
                    (serviceType.Contains(":WANIPConnection:") || serviceType.Contains(":WANPPPConnection:")))
                {
                    if (!controlUrl.StartsWith("http"))
                    {
                        var uri = new Uri(location);
                        if (controlUrl.StartsWith('/'))
                        {
                            controlUrl = uri.Scheme + "://" + uri.Host + ":" + uri.Port + controlUrl;
                        }
                        else
                        {
                            controlUrl = uri.Scheme + "://" + uri.Host + ":" + uri.Port + "/" + controlUrl;
                        }
                    }

                    return new UpnpGateway
                    {
                        Name = friendlyName,
                        ControlUrl = controlUrl,
                        ServiceType = serviceType,
                        LocalAddress = localIp
                    };
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "UPnP HTTP error fetching {Location}", location);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout - common for UPnP discovery
            _logger.LogDebug(ex, "UPnP timeout fetching {Location}", location);
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogWarning(ex, "UPnP XML parse error for {Location}", location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UPnP error parsing {Location}", location);
        }
        return null;
    }

    private static string? GetHeaderValue(string response, string header)
    {
        using var reader = new StringReader(response);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(header.Length + 1).Trim();
            }
        }
        return null;
    }

    private static IEnumerable<IPAddress> GetLocalIPs()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);
    }

    private static async Task ReceiveLoopAsync(
        UdpClient client,
        List<UpnpGateway> gateways,
        IPAddress localIp,
        Func<string, IPAddress, CancellationToken, Task<UpnpGateway?>> parseDescriptionAsync,
        CancellationToken ct)
    {
        while (client.Client != null && !ct.IsCancellationRequested)
        {
            try
            {
                var res = await client.ReceiveAsync(ct).ConfigureAwait(false);
                var response = Encoding.ASCII.GetString(res.Buffer);

                string? location = GetHeaderValue(response, "LOCATION");
                if (!string.IsNullOrEmpty(location))
                {
                    // Check if we already found this gateway
                    lock (gateways)
                    {
                        if (gateways.Any(g => g.ControlUrl.Contains(location) || location.Contains(g.ControlUrl)))
                        {
                            continue;
                        }
                    }

                    var gateway = await parseDescriptionAsync(location, localIp, ct).ConfigureAwait(false);
                    if (gateway != null)
                    {
                        lock (gateways)
                        {
                            if (!gateways.Any(g => g.ControlUrl == gateway.ControlUrl))
                            {
                                gateways.Add(gateway);
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Socket closed - expected during shutdown
                break;
            }
            catch (SocketException)
            {
                // Network error - stop this receive loop
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UPnP receive error");
                break;
            }
        }
    }
}

internal class UpnpGateway
{
    public string ControlUrl { get; set; } = string.Empty;
    public IPAddress LocalAddress { get; set; } = IPAddress.Any;
    public string Name { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
}

internal class UpnpPortMapping : IPortMapper
{
    private readonly Func<CancellationToken, Task<List<UpnpGateway>>> _discoverGatewaysAsync;
    private readonly ILogger<UpnpPortMapping> _logger = TorrentLoggerFactory.CreateLogger<UpnpPortMapping>();
    private readonly List<(int Port, string Protocol, string Description)> _mappings = new();
    private readonly Dictionary<UpnpGateway, (PortMappingResult MappingResult, string? Error)> _status = new();
    private List<UpnpGateway> _gateways = new();

    public UpnpPortMapping()
        : this(UpnpDiscovery.DiscoverAsync)
    {
    }

    internal UpnpPortMapping(Func<CancellationToken, Task<List<UpnpGateway>>> discoverGatewaysAsync)
    {
        _discoverGatewaysAsync = discoverGatewaysAsync;
    }

    public string Name => "UPnP";

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
                        $"{Name} ({kvp.Key.Name})",
                        kvp.Value.MappingResult,
                        _mappings.LastOrDefault().Port != 0 ? _mappings.LastOrDefault().Port : null,
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

        lock (_mappings)
        {
            _mappings.Add((port, protocol, description));
        }

        bool anySuccess = false;
        foreach (var gateway in _gateways)
        {
            if (await MapOnGatewayAsync(gateway, port, protocol, description, ct).ConfigureAwait(false))
            {
                anySuccess = true;
                lock (_status)
                {
                    _status[gateway] = (PortMappingResult.Success, null);
                }
            }
            else
            {
                lock (_status)
                {
                    _status[gateway] = (PortMappingResult.Failed, "Mapping failed");
                }
            }
        }
        return anySuccess;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _gateways = await _discoverGatewaysAsync(ct).ConfigureAwait(false);
        lock (_status)
        {
            _status.Clear();
            foreach (var g in _gateways)
            {
                _status[g] = (PortMappingResult.Pending, null);
            }
        }

        if (_gateways.Count > 0)
        {
            foreach (var g in _gateways)
            {
                _logger.LogInformation("UPnP: Found gateway {GatewayName} at {GatewayAddress}", g.Name, g.LocalAddress);
            }
        }
        else
        {
            _logger.LogInformation("UPnP: No gateways found");
        }
    }

    public async Task UnmapAllAsync(CancellationToken ct)
    {
        if (_gateways.Count == 0)
        {
            return;
        }

        List<(int Port, string Protocol, string Description)> toUnmap;
        lock (_mappings)
        {
            toUnmap = _mappings.ToList();
            _mappings.Clear();
        }

        foreach (var mapping in toUnmap)
        {
            foreach (var gateway in _gateways)
            {
                await UnmapOnGatewayAsync(gateway, mapping.Port, mapping.Protocol, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> MapOnGatewayAsync(UpnpGateway gateway, int port, string protocol, string description, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append($"<u:AddPortMapping xmlns:u=\"{gateway.ServiceType}\">");
        sb.Append("<NewRemoteHost></NewRemoteHost>");
        sb.Append($"<NewExternalPort>{port}</NewExternalPort>");
        sb.Append($"<NewProtocol>{protocol}</NewProtocol>");
        sb.Append($"<NewInternalPort>{port}</NewInternalPort>");
        sb.Append($"<NewInternalClient>{gateway.LocalAddress}</NewInternalClient>");
        sb.Append("<NewEnabled>1</NewEnabled>");
        sb.Append($"<NewPortMappingDescription>{description}</NewPortMappingDescription>");
        sb.Append("<NewLeaseDuration>0</NewLeaseDuration>");
        sb.Append("</u:AddPortMapping>");
        sb.Append("</s:Body>");
        sb.Append("</s:Envelope>");

        if (await SendSoapRequestAsync(gateway, sb.ToString(), "AddPortMapping", ct).ConfigureAwait(false))
        {
            _logger.LogInformation("UPnP: Mapped {Protocol} port {Port} on {GatewayName}", protocol, port, gateway.Name);
            return true;
        }
        else
        {
            if (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("UPnP: Failed to map {Protocol} port {Port} on {GatewayName}", protocol, port, gateway.Name);
            }
            return false;
        }
    }

    private async Task<bool> SendSoapRequestAsync(UpnpGateway gateway, string body, string action, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10) // UPnP requests should complete quickly
            };
            var content = new StringContent(body, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{gateway.ServiceType}#{action}\"");

            var response = await client.PostAsync(gateway.ControlUrl, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "UPnP error on {GatewayName}", gateway.Name);
            }
            return false;
        }
    }

    private async Task UnmapOnGatewayAsync(UpnpGateway gateway, int port, string protocol, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append($"<u:DeletePortMapping xmlns:u=\"{gateway.ServiceType}\">");
        sb.Append("<NewRemoteHost></NewRemoteHost>");
        sb.Append($"<NewExternalPort>{port}</NewExternalPort>");
        sb.Append($"<NewProtocol>{protocol}</NewProtocol>");
        sb.Append("</u:DeletePortMapping>");
        sb.Append("</s:Body>");
        sb.Append("</s:Envelope>");

        await SendSoapRequestAsync(gateway, sb.ToString(), "DeletePortMapping", ct).ConfigureAwait(false);
    }
}
