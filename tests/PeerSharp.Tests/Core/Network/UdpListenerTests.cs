using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class UdpListenerTests
{
    private class MockUdpSocket : IUdpSocket
    {
        public bool IgnoreCancellation { get; init; }
        public List<byte[]> SentPackets { get; } = [];
        private readonly System.Threading.Channels.Channel<UdpReceiveResult> _receiveChannel =
            System.Threading.Channels.Channel.CreateUnbounded<UdpReceiveResult>();

        public Socket Client { get; } = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public void EnqueueReceive(byte[] data, IPEndPoint remote)
        {
            _receiveChannel.Writer.TryWrite(new UdpReceiveResult(data, remote));
        }

        public void JoinMulticastGroup(IPAddress multicastAddr, IPAddress? localInterface = null) { }
        public void Close() { }
        public void Dispose()
        {
            Client.Dispose();
        }

        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            return await _receiveChannel.Reader.ReadAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
        {
            SentPackets.Add(datagram.ToArray());
            return new ValueTask<int>(datagram.Length);
        }
    }

    private class MockUdpSocketFactory : IUdpSocketFactory
    {
        public MockUdpSocket LastSocket { get; }

        public MockUdpSocketFactory(bool ignoreCancellation = false)
        {
            LastSocket = new MockUdpSocket { IgnoreCancellation = ignoreCancellation };
        }
        public IUdpSocket Create(int port)
        {
            return LastSocket;
        }

        public IUdpSocket Create(AddressFamily family)
        {
            return LastSocket;
        }
    }

    private class MockReceiver : IUdpReceiver
    {
        public List<(byte[] Data, IPEndPoint Remote)> Received { get; } = [];
        public void Receive(byte[] data, IPEndPoint remote)
        {
            Received.Add((data, remote));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_DispatchesToReceiver()
    {
        var settings = new Settings();
        var factory = new MockUdpSocketFactory();
        var listener = new UdpListener(5000, factory, settings);
        var receiver = new MockReceiver();

        listener.RegisterReceiver(receiver);
        await listener.StartAsync();

        var data = new byte[] { 1, 2, 3 };
        var remote = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1234);
        factory.LastSocket.EnqueueReceive(data, remote);

        // Wait for dispatch
        int attempts = 0;
        while (receiver.Received.Count == 0 && attempts++ < 100)
        {
            await Task.Delay(10);
        }

        Assert.Single(receiver.Received);
        Assert.Equal(data, receiver.Received[0].Data);
        Assert.Equal(remote, receiver.Received[0].Remote);

        await listener.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WithBindAddress_BindsSharedSocketToThatAddress()
    {
        var bindAddress = IPAddress.Parse("127.0.0.2");
        var settings = new Settings
        {
            Connection = { BindAddress = bindAddress }
        };
        var factory = new MockUdpSocketFactory();
        var listener = new UdpListener(0, factory, settings);

        await listener.StartAsync(TestContext.Current.CancellationToken);

        var endpoint = Assert.IsType<IPEndPoint>(factory.LastSocket.Client.LocalEndPoint);
        Assert.Equal(bindAddress, endpoint.Address);
        await listener.DisposeAsync();
    }

    /// <summary>
    /// A proxy that cannot carry UDP must stop the socket opening, not be worked around.
    /// </summary>
    /// <remarks>
    /// The listener carries the DHT and uTP, so a direct bind here announces the real address to
    /// every DHT node while the traffic the proxy was configured for goes through it. libtorrent
    /// refuses the send in the same situation rather than falling back.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task StartAsync_WithAProxyThatCannotCarryUdp_RefusesToBind()
    {
        var settings = new Settings();
        settings.Proxy.Type = ProxyType.Http;
        settings.Proxy.Host = "127.0.0.1";
        settings.Proxy.Port = 8080;

        var factory = new MockUdpSocketFactory();
        var listener = new UdpListener(0, factory, settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.StartAsync(TestContext.Current.CancellationToken));

        // Refusing to start is the whole behaviour: nothing can be sent from a listener that never
        // opened, and the message has to say why or the failure looks like a bug in the proxy setup.
        Assert.Contains("cannot carry UDP", error.Message, StringComparison.Ordinal);
        Assert.Contains("SOCKS5", error.Message, StringComparison.Ordinal);

        await listener.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task StopAsync_DoesNotWaitForNonCooperativeReceiveTask()
    {
        var factory = new MockUdpSocketFactory(ignoreCancellation: true);
        var listener = new UdpListener(5000, factory, new Settings());
        await listener.StartAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await listener.StopAsync();

        // Generous on purpose. What this distinguishes is "returned" from "blocked on the hung
        // dependency", and blocking there is unbounded - so a threshold only has to be clear of
        // how long a loaded CI runner can stall, which has been measured at several seconds.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Stop took {stopwatch.Elapsed}");

        // Release the deliberately non-cooperative receive task so the test leaves no work behind.
        factory.LastSocket.EnqueueReceive([], new IPEndPoint(IPAddress.Loopback, 1));
        await listener.DisposeAsync();
    }
}




