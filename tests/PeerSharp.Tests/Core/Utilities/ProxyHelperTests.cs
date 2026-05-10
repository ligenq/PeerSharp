using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Utilities;

public class ProxyHelperTests
{
    [Fact]
    public void Socks5Udp_RoundTrip_IPv4()
    {
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

        byte[] packet = ProxyHelper.GetSocks5UdpPacket(payload, ep);
        var (unwrappedPayload, unwrappedEp) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.Equal(payload, unwrappedPayload.ToArray());
        Assert.Equal(ep, unwrappedEp);
    }

    [Fact]
    public void Socks5Udp_RoundTrip_IPv6()
    {
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        var ep = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1234);

        byte[] packet = ProxyHelper.GetSocks5UdpPacket(payload, ep);
        var (unwrappedPayload, unwrappedEp) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.Equal(payload, unwrappedPayload.ToArray());
        Assert.Equal(ep, unwrappedEp);
    }

    [Fact]
    public void WriteSocks5UdpPacket_IPv4_WritesPacketWithoutAllocation()
    {
        byte[] payload = new byte[] { 9, 8, 7 };
        var ep = new IPEndPoint(IPAddress.Parse("5.6.7.8"), 65000);
        Span<byte> destination = stackalloc byte[13];

        int written = ProxyHelper.WriteSocks5UdpPacket(payload, ep, destination);

        Assert.Equal(13, written);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 5, 6, 7, 8, 253, 232, 9, 8, 7 }, destination.ToArray());
    }

    [Fact]
    public void WriteSocks5UdpPacket_IPv6_WritesPacketWithoutAllocation()
    {
        byte[] payload = new byte[] { 4, 5 };
        var ep = new IPEndPoint(IPAddress.Parse("2001:db8::2"), 6881);
        Span<byte> destination = stackalloc byte[24];

        int written = ProxyHelper.WriteSocks5UdpPacket(payload, ep, destination);

        Assert.Equal(24, written);
        Assert.Equal(0x04, destination[3]);
        Assert.Equal(0x1a, destination[20]);
        Assert.Equal(0xe1, destination[21]);
        Assert.Equal(payload, destination.Slice(22, 2).ToArray());
    }

    [Fact]
    public void WriteSocks5UdpPacket_ThrowsWhenDestinationTooSmall()
    {
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> destination = stackalloc byte[9];
            ProxyHelper.WriteSocks5UdpPacket(new byte[] { 1 }, ep, destination);
        });
    }

    [Fact]
    public void UnwrapSocks5UdpPacket_DomainAddress_ReturnsPayloadWithAnyEndpoint()
    {
        byte[] packet =
        {
            0, 0, 0, 3,
            11,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', (byte)'.', (byte)'c', (byte)'o', (byte)'m',
            0x1a, 0xe1,
            1, 2, 3
        };

        var (payload, endpoint) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.Equal(IPAddress.Any, endpoint.Address);
        Assert.Equal(6881, endpoint.Port);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 9, 1, 2, 3, 4, 0, 1 })]
    public void UnwrapSocks5UdpPacket_InvalidPacket_ReturnsEmptyPayload(byte[] packet)
    {
        var (payload, endpoint) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.True(payload.IsEmpty);
        Assert.Equal(IPAddress.Any, endpoint.Address);
        Assert.Equal(0, endpoint.Port);
    }

    [Fact]
    public async Task ConnectHttpProxyAsync_SucceedsOn200()
    {
        using var server = new HttpProxyTestServer(HttpProxyTestResponse.Success);
        var (stream, client) = await ProxyHelper.ConnectHttpProxyAsync(
            "example.com",
            443,
            server.Host,
            server.Port,
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(stream);
        Assert.True(client.Connected);

        client.Dispose();
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task ConnectHttpProxyAsync_ThrowsOnNon200()
    {
        using var server = new HttpProxyTestServer(HttpProxyTestResponse.AuthRequired);
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.ConnectHttpProxyAsync(
                "example.com",
                443,
                server.Host,
                server.Port,
                null,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ConnectSocks5Async_SucceedsWithoutAuth()
    {
        using var server = new Socks5TestServer(Socks5Mode.NoAuthConnect);
        var (stream, client) = await ProxyHelper.ConnectSocks5Async(
            "1.2.3.4",
            80,
            server.Host,
            server.Port,
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(stream);
        Assert.True(client.Connected);

        client.Dispose();
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task ConnectSocks5Async_ThrowsWhenAuthRequiredWithoutCredentials()
    {
        using var server = new Socks5TestServer(Socks5Mode.RequireAuth);
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.ConnectSocks5Async(
                "1.2.3.4",
                80,
                server.Host,
                server.Port,
                null,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ConnectSocks5UdpAsync_ReturnsUdpEndpoint()
    {
        using var server = new Socks5TestServer(Socks5Mode.UdpAssociate);
        var (udpClient, udpEndpoint, controlClient) = await ProxyHelper.ConnectSocks5UdpAsync(
            server.Host,
            server.Port,
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(udpClient);
        Assert.Equal(server.UdpPort, udpEndpoint.Port);
        Assert.True(controlClient.Connected);

        udpClient.Dispose();
        controlClient.Dispose();
    }

    [Fact]
    public async Task ConnectSocks5UdpAsync_ThrowsOnErrorResponseCode()
    {
        using var server = new Socks5TestServer(Socks5Mode.UdpAssociateErrorCode);
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.ConnectSocks5UdpAsync(
                server.Host,
                server.Port,
                null,
                null,
                CancellationToken.None));

        Assert.Contains("error: 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectSocks5UdpAsync_IPv6RelayAddress_ParsedCorrectly()
    {
        using var server = new Socks5TestServer(Socks5Mode.UdpAssociateIPv6);
        var (udpClient, udpEndpoint, controlClient) = await ProxyHelper.ConnectSocks5UdpAsync(
            server.Host,
            server.Port,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetworkV6, udpEndpoint.AddressFamily);
        Assert.Equal(IPAddress.IPv6Loopback, udpEndpoint.Address);
        Assert.Equal(server.UdpPort, udpEndpoint.Port);

        udpClient.Dispose();
        controlClient.Dispose();
    }

    [Fact]
    public async Task ConnectSocks5UdpAsync_ThrowsWhenConnectionClosedMidAssociation()
    {
        using var server = new Socks5TestServer(Socks5Mode.UdpAssociateConnectionReset);
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.ConnectSocks5UdpAsync(
                server.Host,
                server.Port,
                null,
                null,
                CancellationToken.None));
    }

    private sealed class HttpProxyTestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly HttpProxyTestResponse _response;

        public HttpProxyTestServer(HttpProxyTestResponse response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Host = "127.0.0.1";
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loopTask = Task.Run(AcceptLoopAsync);
        }

        public string Host { get; }
        public int Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _loopTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
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
                var buffer = new byte[1024];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total, 1), _cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                    if (total >= 4 &&
                        buffer[total - 4] == '\r' && buffer[total - 3] == '\n' &&
                        buffer[total - 2] == '\r' && buffer[total - 1] == '\n')
                    {
                        break;
                    }
                }

                string response = _response switch
                {
                    HttpProxyTestResponse.Success => "HTTP/1.1 200 Connection Established\r\n\r\n",
                    HttpProxyTestResponse.AuthRequired => "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n",
                    _ => "HTTP/1.1 500 Error\r\n\r\n"
                };
                var bytes = System.Text.Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private enum HttpProxyTestResponse
    {
        Success,
        AuthRequired
    }

    private sealed class Socks5TestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly Socks5Mode _mode;
        private readonly UdpClient? _udp;

        public Socks5TestServer(Socks5Mode mode)
        {
            _mode = mode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Host = "127.0.0.1";
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (mode == Socks5Mode.UdpAssociate || mode == Socks5Mode.UdpAssociateIPv6)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            }
            _loopTask = Task.Run(AcceptLoopAsync);
        }

        public string Host { get; }
        public int Port { get; }
        public int UdpPort { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _udp?.Dispose();
            try { _loopTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
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
                var methodHeader = await ReadExactAsync(stream, 2).ConfigureAwait(false);
                int methodCount = methodHeader[1];
                if (methodCount > 0)
                {
                    _ = await ReadExactAsync(stream, methodCount).ConfigureAwait(false);
                }

                if (_mode == Socks5Mode.RequireAuth)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x02 }, _cts.Token).ConfigureAwait(false);
                    return;
                }

                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, _cts.Token).ConfigureAwait(false);

                var requestHeader = await ReadExactAsync(stream, 4).ConfigureAwait(false);
                byte atyp = requestHeader[3];
                int addrLen = atyp switch
                {
                    0x01 => 4,
                    0x04 => 16,
                    0x03 => (await ReadExactAsync(stream, 1).ConfigureAwait(false))[0],
                    _ => 0
                };
                if (addrLen > 0)
                {
                    _ = await ReadExactAsync(stream, addrLen).ConfigureAwait(false);
                }
                _ = await ReadExactAsync(stream, 2).ConfigureAwait(false);

                if (_mode == Socks5Mode.NoAuthConnect)
                {
                    byte[] response = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                    return;
                }

                if (_mode == Socks5Mode.UdpAssociate)
                {
                    int port = UdpPort;
                    byte[] response =
                    {
                        0x05, 0x00, 0x00, 0x01,
                        0, 0, 0, 0,
                        (byte)(port >> 8), (byte)(port & 0xFF)
                    };
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                }

                if (_mode == Socks5Mode.UdpAssociateErrorCode)
                {
                    // REP=0x02: connection not allowed by ruleset
                    byte[] response = { 0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                }

                if (_mode == Socks5Mode.UdpAssociateIPv6)
                {
                    // IPv6 relay address ::1 (loopback) — ATYP=0x04
                    var addr6 = IPAddress.IPv6Loopback.GetAddressBytes();
                    int port = UdpPort;
                    var response = new byte[4 + 16 + 2];
                    response[0] = 0x05; response[1] = 0x00; response[2] = 0x00; response[3] = 0x04;
                    addr6.CopyTo(response, 4);
                    response[20] = (byte)(port >> 8);
                    response[21] = (byte)(port & 0xFF);
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                }

                // UdpAssociateConnectionReset: fall through — stream disposal signals EOF to client
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of stream");
                }
                offset += read;
            }
            return buffer;
        }
    }

    private enum Socks5Mode
    {
        NoAuthConnect,
        RequireAuth,
        UdpAssociate,
        UdpAssociateErrorCode,
        UdpAssociateIPv6,
        UdpAssociateConnectionReset
    }
}





