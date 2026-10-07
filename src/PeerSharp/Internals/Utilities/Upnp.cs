using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using System.Security;

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
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static Task<List<UpnpGateway>> DiscoverAsync(CancellationToken ct = default)
    {
        return DiscoverAsync(NullLoggerFactory.Instance, ct);
    }

    public static Task<List<UpnpGateway>> DiscoverAsync(ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger(nameof(UpnpDiscovery));
        return DiscoverAsync(
            GetLocalIPs,
            new IPEndPoint(IPAddress.Parse(SsdpIp), SsdpPort),
            (location, localIp, token) => ParseDescriptionAsync(location, localIp, logger, token),
            TimeProvider.System,
            logger,
            ct);
    }

    internal static async Task<List<UpnpGateway>> DiscoverAsync(
        Func<IEnumerable<IPAddress>> localIpProvider,
        IPEndPoint ssdpEndpoint,
        Func<string, IPAddress, CancellationToken, Task<UpnpGateway?>> parseDescriptionAsync,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        return await DiscoverAsync(localIpProvider, ssdpEndpoint, parseDescriptionAsync, timeProvider, NullLogger.Instance, ct).ConfigureAwait(false);
    }

    private static async Task<List<UpnpGateway>> DiscoverAsync(
        Func<IEnumerable<IPAddress>> localIpProvider,
        IPEndPoint ssdpEndpoint,
        Func<string, IPAddress, CancellationToken, Task<UpnpGateway?>> parseDescriptionAsync,
        TimeProvider? timeProvider,
        ILogger logger,
        CancellationToken ct)
    {
        timeProvider ??= TimeProvider.System;
        var gateways = new List<UpnpGateway>();
        var clients = new List<UdpClient>();
        var tasks = new List<Task>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

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

                    tasks.Add(ReceiveLoopAsync(client, gateways, ip, parseDescriptionAsync, logger, scope.Token));

                    // Send M-SEARCH
                    var data = Encoding.ASCII.GetBytes(SsdpMessage);

                    for (int i = 0; i < 2; i++)
                    {
                        await client.SendAsync(data, ssdpEndpoint, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Bind error on {Ip}", ip);
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in clients)
            {
                c.Close();
            }
        }

        ct.ThrowIfCancellationRequested();
        return gateways;
    }

    internal static async Task<UpnpGateway?> ParseDescriptionAsync(string location, IPAddress localIp, CancellationToken ct)
    {
        return await ParseDescriptionAsync(location, localIp, NullLogger.Instance, ct).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarLint", "S1075:URIs should not be hardcoded", Justification = "URL separator, not path delimiter")]
    private static async Task<UpnpGateway?> ParseDescriptionAsync(string location, IPAddress localIp, ILogger logger, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var descriptionUri) || descriptionUri.Scheme is not ("http" or "https")) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await HttpClient.GetAsync(descriptionUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var doc = await ReadXmlAsync(response, timeout.Token).ConfigureAwait(false);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device == null)
            {
                return null;
            }

            var friendlyName = device.Element(ns + "friendlyName")?.Value ?? "Unknown";
            var baseUri = descriptionUri;
            string? urlBase = doc.Root?.Element(ns + "URLBase")?.Value;
            if (!string.IsNullOrWhiteSpace(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out var advertisedBase)) baseUri = advertisedBase;

            foreach (var service in doc.Descendants(ns + "service"))
            {
                var serviceType = service.Element(ns + "serviceType")?.Value;
                var controlUrl = service.Element(ns + "controlURL")?.Value;

                if (serviceType != null && controlUrl != null &&
                    (serviceType.Contains(":WANIPConnection:") || serviceType.Contains(":WANPPPConnection:")))
                {
                    if (!Uri.TryCreate(baseUri, controlUrl.Trim(), out var controlUri) || controlUri.Scheme is not ("http" or "https")) continue;

                    return new UpnpGateway
                    {
                        Name = friendlyName,
                        ControlUrl = controlUri.AbsoluteUri,
                        ServiceType = serviceType,
                        LocalAddress = localIp
                    };
                }
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "UPnP HTTP error fetching {Location}", location);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            // Timeout - common for UPnP discovery
            logger.LogDebug(ex, "UPnP timeout fetching {Location}", location);
        }
        catch (System.Xml.XmlException ex)
        {
            logger.LogWarning(ex, "UPnP XML parse error for {Location}", location);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UPnP error parsing {Location}", location);
        }
        return null;
    }

    internal static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new XmlException("UPnP XML response is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new XmlException("UPnP XML response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxBytes });
        return XDocument.Load(reader);
    }

    private static string? GetHeaderValue(string response, string header)
    {
        using var reader = new StringReader(response);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line[(header.Length + 1)..].Trim();
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
        ILogger logger,
        CancellationToken ct)
    {
        var locations = new HashSet<string>(StringComparer.Ordinal);
        while (client.Client != null && !ct.IsCancellationRequested)
        {
            try
            {
                var res = await client.ReceiveAsync(ct).ConfigureAwait(false);
                var response = Encoding.ASCII.GetString(res.Buffer);

                string? location = GetHeaderValue(response, "LOCATION");
                if (!string.IsNullOrEmpty(location))
                {
                    // Bound unsolicited discovery results and don't fetch the same description twice.
                    lock (gateways)
                    {
                        if (gateways.Count >= 64) break;
                    }
                    if (locations.Count >= 64 || !locations.Add(location)) continue;

                    var gateway = await parseDescriptionAsync(location, localIp, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (gateway != null)
                    {
                        lock (gateways)
                        {
                            if (gateways.Count < 64 && !gateways.Any(g => g.ControlUrl == gateway.ControlUrl))
                            {
                                gateways.Add(gateway);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
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
                logger.LogError(ex, "UPnP receive error");
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
    private static readonly HttpClient SoapClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<CancellationToken, Task<List<UpnpGateway>>> _discoverGatewaysAsync;
    private readonly ILogger<UpnpPortMapping> _logger;
    private readonly List<(int Port, string Protocol, string Description)> _mappings = [];
    private readonly Dictionary<UpnpGateway, (PortMappingResult MappingResult, string? Error, int? ExternalPort)> _status = [];
    private List<UpnpGateway> _gateways = [];
    private readonly HashSet<(UpnpGateway Gateway, int Port, string Protocol)> _successfulMappings = [];

    public UpnpPortMapping()
        : this(UpnpDiscovery.DiscoverAsync, NullLoggerFactory.Instance)
    {
    }

    public UpnpPortMapping(ILoggerFactory loggerFactory)
        : this(ct => UpnpDiscovery.DiscoverAsync(loggerFactory, ct), loggerFactory)
    {
    }

    internal UpnpPortMapping(Func<CancellationToken, Task<List<UpnpGateway>>> discoverGatewaysAsync)
        : this(discoverGatewaysAsync, NullLoggerFactory.Instance)
    {
    }

    internal UpnpPortMapping(Func<CancellationToken, Task<List<UpnpGateway>>> discoverGatewaysAsync, ILoggerFactory loggerFactory)
    {
        _discoverGatewaysAsync = discoverGatewaysAsync;
        _logger = loggerFactory.CreateLogger<UpnpPortMapping>();
    }

    public string Name => "UPnP";

    public int? GetExternalPort(int internalPort, string protocol)
    {
        lock (_status) return _successfulMappings.Any(mapping => mapping.Port == internalPort && mapping.Protocol == protocol) ? internalPort : null;
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
                        $"{Name} ({kvp.Key.Name})",
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
        UpnpGateway[] gateways;
        lock (_status) gateways = [.. _gateways];
        if (gateways.Length == 0)
        {
            return false;
        }

        lock (_mappings)
        {
            if (!_mappings.Any(mapping => mapping.Port == port && mapping.Protocol == protocol)) _mappings.Add((port, protocol, description));
        }

        var results = await Task.WhenAll(gateways.Select(async gateway =>
        {
            bool success = await MapOnGatewayAsync(gateway, port, protocol, description, ct).ConfigureAwait(false);
            lock (_status)
            {
                if (success) _successfulMappings.Add((gateway, port, protocol));
                else _successfulMappings.Remove((gateway, port, protocol));
                _status[gateway] = success
                    ? (PortMappingResult.Success, null, port)
                    : (PortMappingResult.Failed, "Mapping failed", null);
            }
            return success;
        })).ConfigureAwait(false);
        return results.Any(static success => success);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var gateways = await _discoverGatewaysAsync(ct).ConfigureAwait(false);
        lock (_status)
        {
            _gateways = gateways;
            _successfulMappings.RemoveWhere(mapping => !gateways.Contains(mapping.Gateway));
            _status.Clear();
            foreach (var g in _gateways)
            {
                _status[g] = (PortMappingResult.Pending, null, null);
            }
        }

        if (gateways.Count > 0)
        {
            foreach (var g in gateways)
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
        List<(int Port, string Protocol, string Description)> toUnmap;
        lock (_mappings)
        {
            toUnmap = [.. _mappings];
            _mappings.Clear();
        }

        // Fan out across gateways - they are independent devices - but keep the ports for a
        // single gateway sequential. A flat mappings x gateways fan-out is exactly the burst
        // that consumer routers rate-limit or silently drop, turning a slow-but-reliable
        // teardown into a flaky one.
        UpnpGateway[] gateways;
        lock (_status)
        {
            gateways = [.. _gateways];
            _successfulMappings.Clear();
            foreach (var gateway in gateways) _status[gateway] = (PortMappingResult.NotAttempted, null, null);
        }
        await Task.WhenAll(gateways.Select(async gateway =>
        {
            foreach (var (port, protocol, _) in toUnmap)
            {
                await UnmapOnGatewayAsync(gateway, port, protocol, ct).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    private async Task<bool> MapOnGatewayAsync(UpnpGateway gateway, int port, string protocol, string description, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append(CultureInfo.InvariantCulture, $"<u:AddPortMapping xmlns:u=\"{SecurityElement.Escape(gateway.ServiceType)}\">");
        sb.Append("<NewRemoteHost></NewRemoteHost>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewExternalPort>{port}</NewExternalPort>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewProtocol>{protocol}</NewProtocol>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewInternalPort>{port}</NewInternalPort>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewInternalClient>{gateway.LocalAddress}</NewInternalClient>");
        sb.Append("<NewEnabled>1</NewEnabled>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewPortMappingDescription>{SecurityElement.Escape(description)}</NewPortMappingDescription>");
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var content = new StringContent(body, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{gateway.ServiceType}#{action}\"");

            using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUrl) { Content = content };
            using var response = await SoapClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var document = await UpnpDiscovery.ReadXmlAsync(response, timeout.Token).ConfigureAwait(false);
            return !document.Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).Any();
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
        sb.Append(CultureInfo.InvariantCulture, $"<u:DeletePortMapping xmlns:u=\"{SecurityElement.Escape(gateway.ServiceType)}\">");
        sb.Append("<NewRemoteHost></NewRemoteHost>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewExternalPort>{port}</NewExternalPort>");
        sb.Append(CultureInfo.InvariantCulture, $"<NewProtocol>{protocol}</NewProtocol>");
        sb.Append("</u:DeletePortMapping>");
        sb.Append("</s:Body>");
        sb.Append("</s:Envelope>");

        await SendSoapRequestAsync(gateway, sb.ToString(), "DeletePortMapping", ct).ConfigureAwait(false);
    }
}
