using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Network;

public class DualStackUdpListenerTests
{
    [Fact]
    public void Factory_Create_ShouldEnableDualMode()
    {
        // This test verifies that the UdpSocketFactory creates a socket that supports both IPv4 and IPv6.
        // This is critical for modern networking and P2P connectivity.

        var factory = new UdpSocketFactory();
        using var socket = factory.Create(0); // Bind to ephemeral port

        // Assert
        // On Windows/Linux with modern .NET, we expect DualMode to be available and enabled for IPv6 sockets
        // But 'new UdpClient(port)' might default to IPv4.

        // If the socket is IPv4, it definitely doesn't support DualMode (DualMode is a property of IPv6 sockets)
        if (socket.Client.AddressFamily == AddressFamily.InterNetwork)
        {
            // If it's IPv4, we check if we can actually bind to IPv6
            // But we can't change the family of an existing socket.
            // So if it returns InterNetwork, it's NOT dual stack.
            Assert.Fail($"Socket bound to {socket.Client.LocalEndPoint} is IPv4 only (AddressFamily.InterNetwork). It should be InterNetworkV6 with DualMode=true.");
        }

        Assert.Equal(AddressFamily.InterNetworkV6, socket.Client.AddressFamily);
        Assert.True(socket.Client.DualMode, "Socket should have DualMode enabled to support both IPv4 and IPv6");
    }

    [Fact]
    public async Task Factory_Create_ShouldAcceptIPv4AndIPv6()
    {
        var factory = new UdpSocketFactory();
        using var receiver = factory.Create(0);
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        using var senderV4 = new UdpClient(AddressFamily.InterNetwork);
        using var senderV6 = new UdpClient(AddressFamily.InterNetworkV6);

        var data = new byte[] { 1, 2, 3, 4 };

        // Send from IPv4
        await senderV4.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));

        // Receive
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var resultV4 = await receiver.ReceiveAsync(cts.Token);
        Assert.Equal(data, resultV4.Buffer);

        var addr = resultV4.RemoteEndPoint.Address;
        Assert.True(IPAddress.Loopback.Equals(addr) || IPAddress.Loopback.MapToIPv6().Equals(addr),
            $"Expected loopback (127.0.0.1 or ::ffff:127.0.0.1), got {addr}");

        // Send from IPv6
        await senderV6.SendAsync(data, data.Length, new IPEndPoint(IPAddress.IPv6Loopback, port));

        // Receive
        var resultV6 = await receiver.ReceiveAsync(cts.Token);
        Assert.Equal(data, resultV6.Buffer);
        Assert.Equal(IPAddress.IPv6Loopback, resultV6.RemoteEndPoint.Address); // Should be ::1
    }
}
