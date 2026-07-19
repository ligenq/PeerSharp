using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// A fake Stream that feeds pre-configured server response chunks to the reader
/// and captures all bytes written by the client.
/// </summary>
internal sealed class FakeStream : Stream
{
    private readonly Queue<byte[]> _responseChunks;
    private byte[]? _currentChunk;
    private int _currentChunkOffset;
    private readonly MemoryStream _written = new();

    public FakeStream(params byte[][] responseChunks)
    {
        _responseChunks = new Queue<byte[]>(responseChunks);
    }

    /// <summary>All bytes written by the client, in order.</summary>
    public byte[] WrittenBytes => _written.ToArray();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Refill from queue if current chunk is exhausted
        if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
        {
            if (_responseChunks.Count == 0)
            {
                return 0; // EOF
            }

            _currentChunk = _responseChunks.Dequeue();
            _currentChunkOffset = 0;
        }

        int available = _currentChunk.Length - _currentChunkOffset;
        int toCopy = Math.Min(available, count);
        Array.Copy(_currentChunk, _currentChunkOffset, buffer, offset, toCopy);
        _currentChunkOffset += toCopy;
        return toCopy;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _written.Write(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] tmp = new byte[buffer.Length];
        int read = Read(tmp, 0, tmp.Length);
        tmp.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _written.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
}

public class ProxyHelperTests
{
    [Fact]
    public void Socks5Udp_RoundTrip_IPv4()
    {
        byte[] payload = [1, 2, 3, 4];
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1234);

        byte[] packet = ProxyHelper.GetSocks5UdpPacket(payload, ep);
        var (unwrappedPayload, unwrappedEp) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.Equal(payload, unwrappedPayload.ToArray());
        Assert.Equal(ep, unwrappedEp);
    }

    [Fact]
    public void Socks5Udp_RoundTrip_IPv6()
    {
        byte[] payload = [1, 2, 3, 4];
        var ep = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1234);

        byte[] packet = ProxyHelper.GetSocks5UdpPacket(payload, ep);
        var (unwrappedPayload, unwrappedEp) = ProxyHelper.UnwrapSocks5UdpPacket(packet);

        Assert.Equal(payload, unwrappedPayload.ToArray());
        Assert.Equal(ep, unwrappedEp);
    }

    [Fact]
    public void WriteSocks5UdpPacket_IPv4_WritesPacketWithoutAllocation()
    {
        byte[] payload = [9, 8, 7];
        var ep = new IPEndPoint(IPAddress.Parse("5.6.7.8"), 65000);
        Span<byte> destination = stackalloc byte[13];

        int written = ProxyHelper.WriteSocks5UdpPacket(payload, ep, destination);

        Assert.Equal(13, written);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 5, 6, 7, 8, 253, 232, 9, 8, 7 }, destination.ToArray());
    }

    [Fact]
    public void WriteSocks5UdpPacket_IPv6_WritesPacketWithoutAllocation()
    {
        byte[] payload = [4, 5];
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
            ProxyHelper.WriteSocks5UdpPacket([1], ep, destination);
        });
    }

    [Fact]
    public void UnwrapSocks5UdpPacket_DomainAddress_ReturnsPayloadWithAnyEndpoint()
    {
        byte[] packet =
        [
            0, 0, 0, 3,
            11,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', (byte)'.', (byte)'c', (byte)'o', (byte)'m',
            0x1a, 0xe1,
            1, 2, 3
        ];

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

    // -----------------------------------------------------------------------
    // FakeStream-based unit tests for internal Negotiate* methods
    // -----------------------------------------------------------------------

    // --- Helpers ---

    private static TcpClient MakeDummyTcpClient() => new TcpClient();

    private static byte[] Socks5NoAuthResponse() => [0x05, 0x00];
    private static byte[] Socks5UserPassMethodResponse() => [0x05, 0x02];
    private static byte[] Socks5AuthSuccessResponse() => [0x01, 0x00];
    private static byte[] Socks5AuthFailureResponse() => [0x01, 0x01];
    private static byte[] Socks5ConnectSuccessHeaderIPv4() => [0x05, 0x00, 0x00, 0x01];
    private static byte[] Socks5IPv4BoundAddrAndPort() => [127, 0, 0, 1, 0x04, 0x38]; // 127.0.0.1:1080

    // --- SOCKS5 TCP (NegotiateSocks5Async) ---

    [Fact]
    public async Task NegotiateSocks5_NoAuth_IPv4_Succeeds()
    {
        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            Socks5ConnectSuccessHeaderIPv4(),
            Socks5IPv4BoundAddrAndPort()
        );

        using var client = MakeDummyTcpClient();
        var (resultStream, resultClient) = await ProxyHelper.NegotiateSocks5Async(
            stream, client, "1.2.3.4", 80, null, null, CancellationToken.None);

        Assert.Same(stream, resultStream);
        Assert.Same(client, resultClient);

        // Greeting: 05 01 00
        var written = stream.WrittenBytes;
        Assert.Equal(0x05, written[0]);
        Assert.Equal(0x01, written[1]); // 1 method
        Assert.Equal(0x00, written[2]); // NO AUTH
    }

    [Fact]
    public async Task NegotiateSocks5_NoAuth_IPv6_Succeeds()
    {
        var successHeaderIPv6 = new byte[] { 0x05, 0x00, 0x00, 0x04 }; // ATYP=IPv6
        var ipv6AddrAndPort = new byte[18]; // 16 addr + 2 port
        ipv6AddrAndPort[16] = 0x04; ipv6AddrAndPort[17] = 0x38; // port 1080

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            successHeaderIPv6,
            ipv6AddrAndPort
        );

        using var client = MakeDummyTcpClient();
        var (resultStream, _) = await ProxyHelper.NegotiateSocks5Async(
            stream, client, "::1", 443, null, null, CancellationToken.None);

        Assert.Same(stream, resultStream);

        // Greeting is 3 bytes; CONNECT request starts at offset 3
        // CONNECT: 05 01 00 04 [16 addr bytes] [2 port bytes] → ATYP at written[6]
        var written = stream.WrittenBytes;
        Assert.Equal(0x04, written[6]); // ATYP=IPv6
    }

    [Fact]
    public async Task NegotiateSocks5_NoAuth_DomainName_Succeeds()
    {
        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            Socks5ConnectSuccessHeaderIPv4(),
            Socks5IPv4BoundAddrAndPort()
        );

        using var client = MakeDummyTcpClient();
        var (resultStream, _) = await ProxyHelper.NegotiateSocks5Async(
            stream, client, "example.com", 80, null, null, CancellationToken.None);

        Assert.Same(stream, resultStream);

        // Greeting=3 bytes; CONNECT: [05][01][00][03][len][domain...][port hi][port lo]
        var written = stream.WrittenBytes;
        Assert.Equal(0x03, written[6]); // ATYP=domain
        int domainLen = written[7];
        var domain = Encoding.UTF8.GetString(written, 8, domainLen);
        Assert.Equal("example.com", domain);
    }

    [Fact]
    public async Task NegotiateSocks5_UserPassAuth_CorrectCredentials_Succeeds()
    {
        var stream = new FakeStream(
            Socks5UserPassMethodResponse(),
            Socks5AuthSuccessResponse(),
            Socks5ConnectSuccessHeaderIPv4(),
            Socks5IPv4BoundAddrAndPort()
        );

        using var client = MakeDummyTcpClient();
        var (resultStream, _) = await ProxyHelper.NegotiateSocks5Async(
            stream, client, "1.2.3.4", 80, "user", "pass", CancellationToken.None);

        Assert.Same(stream, resultStream);

        // Greeting: 05 02 00 02 (2 methods: NO_AUTH and USER/PASS)
        var written = stream.WrittenBytes;
        Assert.Equal(0x05, written[0]);
        Assert.Equal(0x02, written[1]); // 2 methods
        Assert.Equal(0x00, written[2]);
        Assert.Equal(0x02, written[3]);

        // Auth sub-request at byte 4: [01][userLen][user...][passLen][pass...]
        Assert.Equal(0x01, written[4]); // subneg version
        int userLen = written[5];
        var sentUser = Encoding.UTF8.GetString(written, 6, userLen);
        Assert.Equal("user", sentUser);
        int passLen = written[6 + userLen];
        var sentPass = Encoding.UTF8.GetString(written, 7 + userLen, passLen);
        Assert.Equal("pass", sentPass);
    }

    [Fact]
    public async Task NegotiateSocks5_UserPassAuth_ServerRejectsCredentials_ThrowsIOException()
    {
        var stream = new FakeStream(
            Socks5UserPassMethodResponse(),
            Socks5AuthFailureResponse()
        );

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateSocks5Async(stream, client, "1.2.3.4", 80, "user", "wrongpass", CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateSocks5_WrongVersion_ThrowsIOException()
    {
        var stream = new FakeStream([0x04, 0x00]); // version 4 instead of 5

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateSocks5Async(stream, client, "1.2.3.4", 80, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateSocks5_UnsupportedAuthMethod_ThrowsIOException()
    {
        var stream = new FakeStream([0x05, 0xFF]); // 0xFF = no acceptable methods

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateSocks5Async(stream, client, "1.2.3.4", 80, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateSocks5_ConnectResponseFailure_ThrowsIOException()
    {
        // REP=0x02 (connection not allowed)
        var connectFailHeader = new byte[] { 0x05, 0x02, 0x00, 0x01 };
        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            connectFailHeader,
            Socks5IPv4BoundAddrAndPort()
        );

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateSocks5Async(stream, client, "1.2.3.4", 80, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateSocks5_ConnectResponse_DomainBoundAddress_ReadsCorrectly()
    {
        // Server responds to CONNECT with ATYP=0x03 (domain) in the bound address field
        var connectSuccessDomain = new byte[] { 0x05, 0x00, 0x00, 0x03 }; // ATYP=domain
        var domainBytes = Encoding.UTF8.GetBytes("proxy.example.com");
        var domainResponsePart = new byte[1 + domainBytes.Length + 2];
        domainResponsePart[0] = (byte)domainBytes.Length;
        domainBytes.CopyTo(domainResponsePart, 1);
        domainResponsePart[domainResponsePart.Length - 2] = 0x04;
        domainResponsePart[domainResponsePart.Length - 1] = 0x38; // port 1080

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            connectSuccessDomain,
            domainResponsePart
        );

        using var client = MakeDummyTcpClient();
        var (resultStream, _) = await ProxyHelper.NegotiateSocks5Async(
            stream, client, "1.2.3.4", 80, null, null, CancellationToken.None);

        Assert.Same(stream, resultStream);
    }

    // --- SOCKS5 UDP (NegotiateSocks5UdpAsync) ---

    [Fact]
    public async Task NegotiateSocks5Udp_NoAuth_IPv4RelayAddress_ReturnsCorrectEndpoint()
    {
        var udpAssocHeader = new byte[] { 0x05, 0x00, 0x00, 0x01 }; // ATYP=IPv4
        var udpAddrAndPort = new byte[] { 10, 0, 0, 1, 0x04, 0xD2 }; // 10.0.0.1:1234

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            udpAssocHeader,
            udpAddrAndPort
        );

        using var client = MakeDummyTcpClient();
        var (udpClient, endPoint, controlClient) = await ProxyHelper.NegotiateSocks5UdpAsync(
            stream, client, "10.0.0.1", null, null, CancellationToken.None);

        udpClient.Dispose();
        Assert.Same(client, controlClient);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), endPoint.Address);
        Assert.Equal(1234, endPoint.Port);
    }

    [Fact]
    public async Task NegotiateSocks5Udp_NoAuth_IPv6RelayAddress_ReturnsCorrectEndpoint()
    {
        var udpAssocHeader = new byte[] { 0x05, 0x00, 0x00, 0x04 }; // ATYP=IPv6
        var addr = IPAddress.Parse("::1").GetAddressBytes();
        var portBytes = new byte[] { 0x13, 0x88 }; // 5000
        var addrAndPort = addr.Concat(portBytes).ToArray();

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            udpAssocHeader,
            addrAndPort
        );

        using var client = MakeDummyTcpClient();
        var (udpClient, endPoint, _) = await ProxyHelper.NegotiateSocks5UdpAsync(
            stream, client, "::1", null, null, CancellationToken.None);

        udpClient.Dispose();
        Assert.Equal(IPAddress.Parse("::1"), endPoint.Address);
        Assert.Equal(5000, endPoint.Port);
    }

    [Fact]
    public async Task NegotiateSocks5Udp_AuthPath_Succeeds()
    {
        var udpAssocHeader = new byte[] { 0x05, 0x00, 0x00, 0x01 };
        var udpAddrAndPort = new byte[] { 192, 168, 1, 1, 0x1F, 0x90 }; // 192.168.1.1:8080

        var stream = new FakeStream(
            Socks5UserPassMethodResponse(),
            Socks5AuthSuccessResponse(),
            udpAssocHeader,
            udpAddrAndPort
        );

        using var client = MakeDummyTcpClient();
        var (udpClient, endPoint, _) = await ProxyHelper.NegotiateSocks5UdpAsync(
            stream, client, "192.168.1.1", "user", "pass", CancellationToken.None);

        udpClient.Dispose();
        Assert.Equal(IPAddress.Parse("192.168.1.1"), endPoint.Address);
        Assert.Equal(8080, endPoint.Port);
    }

    [Fact]
    public async Task NegotiateSocks5Udp_AssociateFailure_ThrowsIOException()
    {
        // UDP ASSOCIATE reply code = 0x01 (general failure)
        var udpAssocFailHeader = new byte[] { 0x05, 0x01, 0x00, 0x01 };
        var udpAddrAndPort = new byte[] { 10, 0, 0, 1, 0x04, 0xD2 };

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            udpAssocFailHeader,
            udpAddrAndPort
        );

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateSocks5UdpAsync(stream, client, "10.0.0.1", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateSocks5Udp_ProxyReturns0000_FallsBackToProxyHost()
    {
        // Relay address = 0.0.0.0 → should fall back to proxyHost
        var udpAssocHeader = new byte[] { 0x05, 0x00, 0x00, 0x01 };
        var udpAddrAndPort = new byte[] { 0, 0, 0, 0, 0x04, 0xD2 }; // 0.0.0.0:1234

        var stream = new FakeStream(
            Socks5NoAuthResponse(),
            udpAssocHeader,
            udpAddrAndPort
        );

        using var client = MakeDummyTcpClient();
        // Use a valid IP as proxyHost to avoid real DNS resolution
        var (udpClient, endPoint, _) = await ProxyHelper.NegotiateSocks5UdpAsync(
            stream, client, "127.0.0.1", null, null, CancellationToken.None);

        udpClient.Dispose();
        Assert.NotEqual(IPAddress.Any, endPoint.Address);
        Assert.Equal(1234, endPoint.Port);
    }

    // --- HTTP CONNECT (NegotiateHttpProxyAsync) ---

    private static byte[] BuildHttpResponse(string statusLine)
    {
        var response = statusLine + "\r\n\r\n";
        return Encoding.UTF8.GetBytes(response);
    }

    [Fact]
    public async Task NegotiateHttpProxy_NoAuth_200Ok_Succeeds()
    {
        // Feed response one byte at a time to exercise the read loop
        var responseBytes = BuildHttpResponse("HTTP/1.1 200 Connection established");
        var chunks = responseBytes.Select(b => new byte[] { b }).ToArray();

        var stream = new FakeStream(chunks);

        using var client = MakeDummyTcpClient();
        var (resultStream, resultClient) = await ProxyHelper.NegotiateHttpProxyAsync(
            stream, client, "example.com", 443, null, null, CancellationToken.None);

        Assert.Same(stream, resultStream);
        Assert.Same(client, resultClient);

        var written = Encoding.UTF8.GetString(stream.WrittenBytes);
        Assert.Contains("CONNECT example.com:443 HTTP/1.1", written);
        Assert.DoesNotContain("Proxy-Authorization", written);
    }

    [Fact]
    public async Task NegotiateHttpProxy_WithAuth_SendsProxyAuthorizationHeader()
    {
        var responseBytes = BuildHttpResponse("HTTP/1.1 200 Connection established");
        var chunks = responseBytes.Select(b => new byte[] { b }).ToArray();

        var stream = new FakeStream(chunks);

        using var client = MakeDummyTcpClient();
        await ProxyHelper.NegotiateHttpProxyAsync(
            stream, client, "example.com", 80, "alice", "s3cr3t", CancellationToken.None);

        var written = Encoding.UTF8.GetString(stream.WrittenBytes);
        Assert.Contains("Proxy-Authorization: Basic ", written);

        var expectedB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cr3t"));
        Assert.Contains(expectedB64, written);
    }

    [Fact]
    public async Task NegotiateHttpProxy_Non200Response_ThrowsIOException()
    {
        var responseBytes = BuildHttpResponse("HTTP/1.1 407 Proxy Authentication Required");
        var chunks = responseBytes.Select(b => new byte[] { b }).ToArray();

        var stream = new FakeStream(chunks);

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateHttpProxyAsync(stream, client, "example.com", 80, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NegotiateHttpProxy_ConnectionClosedImmediately_ThrowsIOException()
    {
        // FakeStream with no response chunks → Read returns 0 immediately (EOF)
        var stream = new FakeStream();

        using var client = MakeDummyTcpClient();
        await Assert.ThrowsAsync<IOException>(() =>
            ProxyHelper.NegotiateHttpProxyAsync(stream, client, "example.com", 80, null, null, CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // End of FakeStream-based tests
    // -----------------------------------------------------------------------

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
                    byte[] response = [0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                    return;
                }

                if (_mode == Socks5Mode.UdpAssociate)
                {
                    int port = UdpPort;
                    byte[] response =
                    [
                        0x05, 0x00, 0x00, 0x01,
                        0, 0, 0, 0,
                        (byte)(port >> 8), (byte)(port & 0xFF)
                    ];
                    await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                }

                if (_mode == Socks5Mode.UdpAssociateErrorCode)
                {
                    // REP=0x02: connection not allowed by ruleset
                    byte[] response = [0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
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





