using PeerSharp.Internals.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public sealed class UpnpTests
{
    [Fact]
    public async Task DiscoverAsync_FindsGatewayFromSsdpResponse()
    {
        await using var httpServer = new TestHttpServer(req =>
        {
            if (req.Path == "/desc.xml")
            {
                return TestHttpResponse.Ok(UpnpTestData.DeviceDescriptionXml);
            }
            return TestHttpResponse.NotFound();
        });

        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ssdpEndpoint = (IPEndPoint)udpServer.Client.LocalEndPoint!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var responderTask = RespondToSsdpAsync(udpServer, httpServer.BaseUri + "desc.xml", cts.Token);

        var gateways = await UpnpDiscovery.DiscoverAsync(
            () => new[] { IPAddress.Loopback },
            ssdpEndpoint,
            UpnpDiscovery.ParseDescriptionAsync,
            TimeProvider.System,
            cts.Token);

        await responderTask;

        Assert.Single(gateways);
        var gateway = gateways[0];
        Assert.Equal("TestGateway", gateway.Name);
        Assert.Equal(IPAddress.Loopback, gateway.LocalAddress);
        Assert.EndsWith("/control", gateway.ControlUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WANIPConnection", gateway.ServiceType);
    }

    [Fact]
    public async Task PortMapping_SendsSoapForMapAndUnmap()
    {
        var requests = new ConcurrentQueue<TestHttpRequest>();
        await using var httpServer = new TestHttpServer(req =>
        {
            requests.Enqueue(req);
            return TestHttpResponse.Ok("<xml/>");
        });

        var gateway = new UpnpGateway
        {
            Name = "TestGateway",
            LocalAddress = IPAddress.Loopback,
            ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
            ControlUrl = httpServer.BaseUri + "control"
        };

        var mapper = new UpnpPortMapping(_ => Task.FromResult(new List<UpnpGateway> { gateway }));
        await mapper.StartAsync(CancellationToken.None);

        bool mapped = await mapper.MapPortAsync(12345, "TCP", "TestMap", CancellationToken.None);
        Assert.True(mapped);

        await mapper.UnmapAllAsync(CancellationToken.None);

        Assert.Equal(2, requests.Count);

        Assert.True(requests.TryDequeue(out var addReq));
        Assert.Equal("POST", addReq.Method);
        Assert.Contains("AddPortMapping", addReq.Headers.GetValueOrDefault("SOAPACTION", ""));
        Assert.Contains("<NewExternalPort>12345</NewExternalPort>", addReq.Body);
        Assert.Contains("<NewProtocol>TCP</NewProtocol>", addReq.Body);
        Assert.Contains("<NewPortMappingDescription>TestMap</NewPortMappingDescription>", addReq.Body);

        Assert.True(requests.TryDequeue(out var delReq));
        Assert.Equal("POST", delReq.Method);
        Assert.Contains("DeletePortMapping", delReq.Headers.GetValueOrDefault("SOAPACTION", ""));
    }

    [Fact]
    public async Task GetStatus_NoGateways_ReturnsFailed()
    {
        var mapper = new UpnpPortMapping(_ => Task.FromResult(new List<UpnpGateway>()));
        await mapper.StartAsync(CancellationToken.None);

        var status = mapper.GetStatus();
        Assert.Single(status);
        Assert.Equal(PortMappingResult.Failed, status[0].Result);
        Assert.Equal("No gateways discovered", status[0].ErrorMessage);
    }

    [Fact]
    public async Task ParseDescriptionAsync_InvalidXml_ReturnsNull()
    {
        await using var httpServer = new TestHttpServer(_ => TestHttpResponse.Ok("<not-xml"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var gateway = await UpnpDiscovery.ParseDescriptionAsync(httpServer.BaseUri + "desc.xml", IPAddress.Loopback, cts.Token);

        Assert.Null(gateway);
    }

    [Fact]
    public async Task ParseDescriptionAsync_NoService_ReturnsNull()
    {
        const string xml = """
<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <device>
    <friendlyName>TestGateway</friendlyName>
  </device>
</root>
""";
        await using var httpServer = new TestHttpServer(_ => TestHttpResponse.Ok(xml));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var gateway = await UpnpDiscovery.ParseDescriptionAsync(httpServer.BaseUri + "desc.xml", IPAddress.Loopback, cts.Token);

        Assert.Null(gateway);
    }

    private static async Task RespondToSsdpAsync(UdpClient server, string location, CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            var res = await server.ReceiveAsync(ct).ConfigureAwait(false);
            var response =
                "HTTP/1.1 200 OK\r\n" +
                $"LOCATION: {location}\r\n" +
                "\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            await server.SendAsync(bytes, res.RemoteEndPoint).ConfigureAwait(false);
        }
    }

    private static class UpnpTestData
    {
        public const string DeviceDescriptionXml = """
<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <device>
    <friendlyName>TestGateway</friendlyName>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
        <controlURL>/control</controlURL>
      </service>
    </serviceList>
  </device>
</root>
""";
    }

    private sealed class TestHttpServer : IAsyncDisposable
    {
        private readonly Func<TestHttpRequest, TestHttpResponse> _handler;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoopTask;

        public TestHttpServer(Func<TestHttpRequest, TestHttpResponse> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUri = $"http://127.0.0.1:{port}/";
            _acceptLoopTask = Task.Run(AcceptLoopAsync);
        }

        public string BaseUri { get; }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown.
            }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
                if (request == null)
                {
                    return;
                }

                var response = _handler(request);
                var responseBytes = Encoding.UTF8.GetBytes(response.ToHttpString());
                await stream.WriteAsync(responseBytes, _cts.Token).ConfigureAwait(false);
            }
        }

        private static async Task<TestHttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>();
            var temp = new byte[1024];
            int headerEndIndex = -1;

            while (headerEndIndex < 0)
            {
                int read = await stream.ReadAsync(temp.AsMemory(0, temp.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                buffer.AddRange(temp.AsSpan(0, read).ToArray());
                headerEndIndex = FindHeaderEnd(buffer);
            }

            var headerBytes = buffer.Take(headerEndIndex).ToArray();
            var remaining = buffer.Skip(headerEndIndex + 4).ToArray();
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestLine = lines[0].Split(' ');
            var method = requestLine[0];
            var path = requestLine[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(':');
                if (idx > 0)
                {
                    headers[lines[i][..idx]] = lines[i][(idx + 1)..].Trim();
                }
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var lenStr))
            {
                int.TryParse(lenStr, out contentLength);
            }

            string body = string.Empty;
            if (contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                int offset = 0;

                int copy = Math.Min(contentLength, remaining.Length);
                if (copy > 0)
                {
                    Array.Copy(remaining, 0, bodyBytes, 0, copy);
                    offset = copy;
                }

                while (offset < contentLength)
                {
                    int read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }
                body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
            }

            return new TestHttpRequest(method, path, headers, body);
        }

        private static int FindHeaderEnd(List<byte> buffer)
        {
            for (int i = 0; i <= buffer.Count - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }
    }

    private sealed record TestHttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private sealed record TestHttpResponse(int StatusCode, string Body)
    {
        public static TestHttpResponse Ok(string body) => new(200, body);
        public static TestHttpResponse NotFound() => new(404, "Not Found");

        public string ToHttpString()
        {
            var bytes = Encoding.UTF8.GetByteCount(Body);
            var reason = StatusCode == 200 ? "OK" : "Not Found";
            return $"HTTP/1.1 {StatusCode} {reason}\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n{Body}";
        }
    }
}




