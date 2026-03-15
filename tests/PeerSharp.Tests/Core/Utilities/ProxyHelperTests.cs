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
            if (mode == Socks5Mode.UdpAssociate)
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
        UdpAssociate
    }
}





