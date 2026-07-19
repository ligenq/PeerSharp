using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Internals.Utilities;

internal static class ProxyHelper
{
    private static readonly ILogger Logger = TorrentLoggerFactory.CreateLogger(nameof(ProxyHelper));

    public static async Task<(Stream Stream, TcpClient Client)> ConnectHttpProxyAsync(string targetHost, int targetPort, string proxyHost, int proxyPort, string? username, string? password, CancellationToken cancellationToken)
    {
        Logger.LogDebug("HTTP Proxy: Connecting to proxy {ProxyHost}:{ProxyPort} for target {TargetHost}:{TargetPort}", proxyHost, proxyPort, targetHost, targetPort);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(proxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
            var stream = tcpClient.GetStream();
            return await NegotiateHttpProxyAsync(stream, tcpClient, targetHost, targetPort, username, password, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            tcpClient.Dispose();
            throw;
        }
    }

    internal static async Task<(Stream Stream, TcpClient Client)> NegotiateHttpProxyAsync(
        Stream stream, TcpClient client,
        string targetHost, int targetPort,
        string? username, string? password,
        CancellationToken cancellationToken)
    {
        var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n" +
                             $"Host: {targetHost}:{targetPort}\r\n";

        if (!string.IsNullOrEmpty(username))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            connectRequest += $"Proxy-Authorization: Basic {auth}\r\n";
        }

        connectRequest += "\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(connectRequest), cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[4096];
        int totalRead = 0;
        do
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Unexpected end of stream");
            }

            totalRead += read;
        }
        while (totalRead < 4 ||
                buffer[totalRead - 4] != '\r' || buffer[totalRead - 3] != '\n' ||
                buffer[totalRead - 2] != '\r' || buffer[totalRead - 1] != '\n');

        var response = Encoding.UTF8.GetString(buffer, 0, totalRead);
        var statusLine = response.Split("\r\n")[0];

        // Validate HTTP status line format: "HTTP/x.x 200 ..."
        if (!statusLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            !statusLine.Contains(" 200 "))
        {
            throw new IOException($"HTTP Proxy connection failed: {statusLine}");
        }

        Logger.LogDebug("HTTP Proxy: Connection established to {TargetHost}:{TargetPort}", targetHost, targetPort);
        return (stream, client);
    }

    public static async Task<(Stream Stream, TcpClient Client)> ConnectSocks5Async(string targetHost, int targetPort, string proxyHost, int proxyPort, string? username, string? password, CancellationToken cancellationToken)
    {
        Logger.LogDebug("SOCKS5 TCP: Connecting to proxy {ProxyHost}:{ProxyPort} for target {TargetHost}:{TargetPort}", proxyHost, proxyPort, targetHost, targetPort);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(proxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
            var stream = tcpClient.GetStream();
            return await NegotiateSocks5Async(stream, tcpClient, targetHost, targetPort, username, password, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            tcpClient.Dispose();
            throw;
        }
    }

    internal static async Task<(Stream Stream, TcpClient Client)> NegotiateSocks5Async(
        Stream stream, TcpClient client,
        string targetHost, int targetPort,
        string? username, string? password,
        CancellationToken cancellationToken)
    {
        // 1. Version identifier/method selection message
        byte[] authMethods = string.IsNullOrEmpty(username)
            ? [0x05, 0x01, 0x00]
            : [0x05, 0x02, 0x00, 0x02];

        await stream.WriteAsync(authMethods, cancellationToken).ConfigureAwait(false);

        // 2. Method selection response
        byte[] methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse, cancellationToken).ConfigureAwait(false);

        if (methodResponse[0] != 0x05)
        {
            throw new IOException("Invalid SOCKS5 version");
        }

        if (methodResponse[1] == 0x02) // Username/Password Authentication
        {
            if (string.IsNullOrEmpty(username))
            {
                throw new IOException("Proxy requires authentication but none provided");
            }

            var userBytes = Encoding.UTF8.GetBytes(username);
            var passBytes = Encoding.UTF8.GetBytes(password ?? "");
            byte[] authRequest = new byte[3 + userBytes.Length + passBytes.Length];
            authRequest[0] = 0x01; // Subnegotiation version
            authRequest[1] = (byte)userBytes.Length;
            userBytes.CopyTo(authRequest, 2);
            authRequest[2 + userBytes.Length] = (byte)passBytes.Length;
            passBytes.CopyTo(authRequest, 3 + userBytes.Length);

            await stream.WriteAsync(authRequest, cancellationToken).ConfigureAwait(false);

            byte[] authResponse = new byte[2];
            await ReadExactAsync(stream, authResponse, cancellationToken).ConfigureAwait(false);
            if (authResponse[1] != 0x00)
            {
                throw new IOException("SOCKS5 authentication failed");
            }
        }
        else if (methodResponse[1] != 0x00)
        {
            throw new IOException($"SOCKS5 proxy requires unsupported authentication method: {methodResponse[1]}");
        }

        // 3. Connection request
        byte[] request = new byte[1024];
        request[0] = 0x05;
        request[1] = 0x01; // CONNECT
        request[2] = 0x00; // Reserved

        int offset = 4;
        if (IPAddress.TryParse(targetHost, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                request[3] = 0x01; // IPv4
                ip.GetAddressBytes().CopyTo(request, offset);
                offset += 4;
            }
            else
            {
                request[3] = 0x04; // IPv6
                ip.GetAddressBytes().CopyTo(request, offset);
                offset += 16;
            }
        }
        else
        {
            request[3] = 0x03; // Domain name
            var hostBytes = Encoding.UTF8.GetBytes(targetHost);
            request[offset++] = (byte)hostBytes.Length;
            hostBytes.CopyTo(request, offset);
            offset += hostBytes.Length;
        }

        request[offset++] = (byte)(targetPort >> 8);
        request[offset++] = (byte)(targetPort & 0xFF);

        await stream.WriteAsync(request.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);

        // 4. Connection response
        byte[] response = new byte[4];
        await ReadExactAsync(stream, response, cancellationToken).ConfigureAwait(false);

        if (response[1] != 0x00)
        {
            throw new IOException($"SOCKS5 connection failed with error: {response[1]}");
        }

        // Skip address and port in response (read asynchronously)
        int skipBytes = response[3] switch
        {
            0x01 => 4 + 2, // IPv4 (4 address + 2 port)
            0x04 => 16 + 2, // IPv6 (16 address + 2 port)
            0x03 => -1, // Domain - needs special handling
            _ => throw new IOException("Invalid SOCKS5 address type in response")
        };

        if (skipBytes == -1)
        {
            // Domain: read length byte, then skip (length + 2) bytes
            byte[] lenBuf = new byte[1];
            await ReadExactAsync(stream, lenBuf, cancellationToken).ConfigureAwait(false);
            skipBytes = lenBuf[0] + 2; // domain length + 2 port bytes
        }

        byte[] skipBuf = new byte[skipBytes];
        await ReadExactAsync(stream, skipBuf, cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("SOCKS5 TCP: Connection established to {TargetHost}:{TargetPort}", targetHost, targetPort);
        return (stream, client);
    }

    public static async Task<(UdpClient UdpClient, IPEndPoint ProxyUdpEndPoint, TcpClient ControlClient)> ConnectSocks5UdpAsync(string proxyHost, int proxyPort, string? username, string? password, CancellationToken cancellationToken)
    {
        Logger.LogDebug("SOCKS5 UDP: Initiating UDP ASSOCIATE with proxy {ProxyHost}:{ProxyPort}", proxyHost, proxyPort);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(proxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
            var stream = tcpClient.GetStream();
            return await NegotiateSocks5UdpAsync(stream, tcpClient, proxyHost, username, password, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            tcpClient.Dispose();
            throw;
        }
    }

    internal static async Task<(UdpClient UdpClient, IPEndPoint ProxyUdpEndPoint, TcpClient ControlClient)> NegotiateSocks5UdpAsync(
        Stream stream, TcpClient client,
        string proxyHost,
        string? username, string? password,
        CancellationToken cancellationToken)
    {
        // 1. Handshake (Method Selection)
        byte[] authMethods = string.IsNullOrEmpty(username)
            ? [0x05, 0x01, 0x00]
            : [0x05, 0x02, 0x00, 0x02];
        await stream.WriteAsync(authMethods, cancellationToken).ConfigureAwait(false);

        byte[] methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse, cancellationToken).ConfigureAwait(false);
        if (methodResponse[0] != 0x05)
        {
            throw new IOException("Invalid SOCKS5 version");
        }

        if (methodResponse[1] == 0x02) // Auth
        {
            var userBytes = Encoding.UTF8.GetBytes(username!);
            var passBytes = Encoding.UTF8.GetBytes(password ?? "");
            byte[] authRequest = new byte[3 + userBytes.Length + passBytes.Length];
            authRequest[0] = 0x01;
            authRequest[1] = (byte)userBytes.Length;
            userBytes.CopyTo(authRequest, 2);
            authRequest[2 + userBytes.Length] = (byte)passBytes.Length;
            passBytes.CopyTo(authRequest, 3 + userBytes.Length);
            await stream.WriteAsync(authRequest, cancellationToken).ConfigureAwait(false);

            byte[] authResponse = new byte[2];
            await ReadExactAsync(stream, authResponse, cancellationToken).ConfigureAwait(false);
            if (authResponse[1] != 0x00)
            {
                throw new IOException("SOCKS5 authentication failed");
            }
        }
        else if (methodResponse[1] != 0x00)
        {
            throw new IOException($"SOCKS5 proxy requires unsupported authentication method: {methodResponse[1]}");
        }

        // 2. UDP ASSOCIATE Request
        byte[] request = [0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        // 3. UDP ASSOCIATE Response
        byte[] response = new byte[4];
        await ReadExactAsync(stream, response, cancellationToken).ConfigureAwait(false);
        if (response[1] != 0x00)
        {
            throw new IOException($"SOCKS5 UDP ASSOCIATE failed with error: {response[1]}");
        }

        IPAddress proxyUdpAddress;
        if (response[3] == 0x01) // IPv4
        {
            byte[] addr = new byte[4];
            await ReadExactAsync(stream, addr, cancellationToken).ConfigureAwait(false);
            proxyUdpAddress = new IPAddress(addr);
        }
        else if (response[3] == 0x04) // IPv6
        {
            byte[] addr = new byte[16];
            await ReadExactAsync(stream, addr, cancellationToken).ConfigureAwait(false);
            proxyUdpAddress = new IPAddress(addr);
        }
        else if (response[3] == 0x03) // Domain
        {
            byte[] lenBuf = new byte[1];
            await ReadExactAsync(stream, lenBuf, cancellationToken).ConfigureAwait(false);
            byte[] host = new byte[lenBuf[0]];
            await ReadExactAsync(stream, host, cancellationToken).ConfigureAwait(false);
            var hostStr = Encoding.UTF8.GetString(host);
            var ips = await Dns.GetHostAddressesAsync(hostStr, cancellationToken).ConfigureAwait(false);
            proxyUdpAddress = ips[0];
        }
        else
        {
            throw new IOException("Invalid address type in SOCKS5 response");
        }

        byte[] portBytes = new byte[2];
        await ReadExactAsync(stream, portBytes, cancellationToken).ConfigureAwait(false);
        int proxyUdpPort = (portBytes[0] << 8) | portBytes[1];

        // If the proxy returns 0.0.0.0, it means use the proxy host we connected to
        if (proxyUdpAddress.Equals(IPAddress.Any) || proxyUdpAddress.Equals(IPAddress.IPv6Any))
        {
            var proxyIps = await Dns.GetHostAddressesAsync(proxyHost, cancellationToken).ConfigureAwait(false);
            proxyUdpAddress = proxyIps[0];
        }

        var proxyUdpEndPoint = new IPEndPoint(proxyUdpAddress, proxyUdpPort);
        var udpClient = new UdpClient(proxyUdpAddress.AddressFamily);

        Logger.LogDebug("SOCKS5 UDP: Association established, relay endpoint {ProxyUdpEndPoint}", proxyUdpEndPoint);
        return (udpClient, proxyUdpEndPoint, client);
    }

    public static byte[] GetSocks5UdpPacket(byte[] payload, IPEndPoint target)
    {
        byte[] header = new byte[target.AddressFamily == AddressFamily.InterNetwork ? 10 : 22];
        header[0] = 0; // Reserved
        header[1] = 0; // Reserved
        header[2] = 0; // Frag

        if (target.AddressFamily == AddressFamily.InterNetwork)
        {
            header[3] = 0x01; // IPv4
            target.Address.GetAddressBytes().CopyTo(header, 4);
            header[8] = (byte)(target.Port >> 8);
            header[9] = (byte)(target.Port & 0xFF);
        }
        else
        {
            header[3] = 0x04; // IPv6
            target.Address.GetAddressBytes().CopyTo(header, 4);
            header[20] = (byte)(target.Port >> 8);
            header[21] = (byte)(target.Port & 0xFF);
        }

        byte[] packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet, header.Length);
        return packet;
    }

    public static int WriteSocks5UdpPacket(ReadOnlySpan<byte> payload, IPEndPoint target, Span<byte> destination)
    {
        int headerLength = target.AddressFamily == AddressFamily.InterNetwork ? 10 : 22;
        int totalLength = headerLength + payload.Length;
        if (destination.Length < totalLength)
        {
            throw new ArgumentException("Destination buffer too small for SOCKS5 UDP packet.", nameof(destination));
        }

        destination[0] = 0; // Reserved
        destination[1] = 0; // Reserved
        destination[2] = 0; // Frag

        if (target.AddressFamily == AddressFamily.InterNetwork)
        {
            destination[3] = 0x01; // IPv4
            target.Address.GetAddressBytes().CopyTo(destination.Slice(4, 4));
            destination[8] = (byte)(target.Port >> 8);
            destination[9] = (byte)(target.Port & 0xFF);
        }
        else
        {
            destination[3] = 0x04; // IPv6
            target.Address.GetAddressBytes().CopyTo(destination.Slice(4, 16));
            destination[20] = (byte)(target.Port >> 8);
            destination[21] = (byte)(target.Port & 0xFF);
        }

        payload.CopyTo(destination[headerLength..]);
        return totalLength;
    }

    /// <summary>
    /// Unwraps a SOCKS5 UDP relay packet, extracting the payload and remote endpoint.
    /// </summary>
    /// <remarks>
    /// For domain-type addresses (ATYP 0x03), the domain name is not resolved and
    /// IPAddress.Any is returned. This is acceptable because:
    /// 1. UDP tracker/DHT responses typically use IP addresses, not domains
    /// 2. The caller can identify the peer by other means if needed
    /// 3. Performing DNS resolution here would add latency and complexity
    /// </remarks>
    public static (ReadOnlyMemory<byte> Payload, IPEndPoint RemoteEndPoint) UnwrapSocks5UdpPacket(byte[] packet)
    {
        if (packet.Length < 4)
        {
            return (ReadOnlyMemory<byte>.Empty, new IPEndPoint(IPAddress.Any, 0));
        }

        int offset = 4;
        IPAddress address;
        if (packet[3] == 0x01) // IPv4
        {
            address = new IPAddress(packet.AsSpan(4, 4).ToArray());
            offset += 4;
        }
        else if (packet[3] == 0x04) // IPv6
        {
            address = new IPAddress(packet.AsSpan(4, 16).ToArray());
            offset += 16;
        }
        else if (packet[3] == 0x03) // Domain - see remarks above
        {
            int len = packet[4];
            address = IPAddress.Any;
            offset += 1 + len;
        }
        else
        {
            return (ReadOnlyMemory<byte>.Empty, new IPEndPoint(IPAddress.Any, 0));
        }

        int port = (packet[offset] << 8) | packet[offset + 1];
        offset += 2;

        return (packet.AsMemory(offset), new IPEndPoint(address, port));
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Unexpected end of stream");
            }

            totalRead += read;
        }
    }
}
