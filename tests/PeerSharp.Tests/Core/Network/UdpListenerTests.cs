using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

        public SocketError? SendError { get; set; }
        public int SendAttempts { get; private set; }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
        {
            SendAttempts++;
            if (SendError is { } error)
            {
                return ValueTask.FromException<int>(new SocketException((int)error));
            }

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
    public async Task StartAsync_WithHttpProxyExcludedFromPeers_BindsForUtpWhenDhtIsDisabled()
    {
        var settings = new Settings();
        settings.Dht.Enabled = false;
        settings.Proxy.Type = ProxyType.Http;
        settings.Proxy.Host = "127.0.0.1";
        settings.Proxy.Port = 8080;
        settings.Proxy.ProxyPeers = false;

        var factory = new MockUdpSocketFactory();
        var listener = new UdpListener(0, factory, settings);

        await listener.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(factory.LastSocket.Client.SafeHandle.IsClosed);
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

    [Theory(Timeout = 30000)]
    [InlineData("0.0.0.0", 6881)]
    [InlineData("253.253.253.253", 6881)]
    [InlineData("203.0.113.9", 0)]
    public async Task SendAsync_ToAnAddressNothingCanBeSentTo_DropsItWithoutAskingTheSocket(string address, int port)
    {
        // DHT nodes and peers hand these out; the socket refuses each with an exception.
        var factory = new MockUdpSocketFactory();
        await using var listener = new UdpListener(5000, factory, new Settings());
        await listener.StartAsync(TestContext.Current.CancellationToken);

        await listener.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse(address), port), TestContext.Current.CancellationToken);

        Assert.Equal(0, factory.LastSocket.SendAttempts);
    }

    [Fact(Timeout = 30000)]
    public async Task SendAsync_ToAnAddressWithNoRoute_StopsTryingOnlyThatAddressForAWhile()
    {
        // A missing internet route must not suppress LAN or loopback traffic in the same family.
        var clock = new FakeTimeProvider();
        var factory = new MockUdpSocketFactory();
        await using var listener = new UdpListener(5000, factory, new Settings(), NullLoggerFactory.Instance, clock);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var ipv6Peer = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);
        factory.LastSocket.SendError = SocketError.NetworkUnreachable;

        await listener.SendAsync(new byte[] { 1 }, ipv6Peer, TestContext.Current.CancellationToken);
        await listener.SendAsync(new byte[] { 2 }, ipv6Peer, TestContext.Current.CancellationToken);
        Assert.Equal(1, factory.LastSocket.SendAttempts);

        // Both families remain usable for other destinations; the failed address is retried later.
        factory.LastSocket.SendError = null;
        await listener.SendAsync(new byte[] { 5 }, new IPEndPoint(IPAddress.IPv6Loopback, 6881), TestContext.Current.CancellationToken);
        await listener.SendAsync(new byte[] { 3 }, new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881), TestContext.Current.CancellationToken);
        clock.Advance(UdpListener.UnroutableAddressBackoff);
        await listener.SendAsync(new byte[] { 4 }, ipv6Peer, TestContext.Current.CancellationToken);

        Assert.Equal([[5], [3], [4]], factory.LastSocket.SentPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task SendAsync_UnreachableIPv4Address_DoesNotBlockOtherIPv4Addresses()
    {
        var factory = new MockUdpSocketFactory();
        await using var listener = new UdpListener(5000, factory, new Settings());
        await listener.StartAsync(TestContext.Current.CancellationToken);
        factory.LastSocket.SendError = SocketError.NetworkUnreachable;
        var unreachable = IPAddress.Parse("203.0.113.9");

        await listener.SendAsync(new byte[] { 1 }, new IPEndPoint(unreachable, 6881), TestContext.Current.CancellationToken);
        factory.LastSocket.SendError = null;
        // Changing ports or using a mapped address does not change the destination's route.
        await listener.SendAsync(new byte[] { 2 }, new IPEndPoint(unreachable.MapToIPv6(), 6882), TestContext.Current.CancellationToken);
        await listener.SendAsync(new byte[] { 3 }, new IPEndPoint(IPAddress.Loopback, 6881), TestContext.Current.CancellationToken);

        Assert.Equal(2, factory.LastSocket.SendAttempts);
        Assert.Equal([[3]], factory.LastSocket.SentPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task SendAsync_ManyUnreachableAddresses_EvictsTheOldestBackoff()
    {
        var clock = new FakeTimeProvider();
        var factory = new MockUdpSocketFactory();
        await using var listener = new UdpListener(5000, factory, new Settings(), NullLoggerFactory.Instance, clock);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        factory.LastSocket.SendError = SocketError.NetworkUnreachable;

        for (int i = 0; i <= UdpListener.MaxUnroutableAddresses; i++)
        {
            await listener.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse($"2001:db8::{i + 1:x}"), 6881), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        factory.LastSocket.SendError = null;
        await listener.SendAsync(new byte[] { 2 }, new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881), TestContext.Current.CancellationToken);
        await listener.SendAsync(new byte[] { 3 }, new IPEndPoint(IPAddress.Parse("2001:db8::2"), 6881), TestContext.Current.CancellationToken);

        Assert.Equal([[2]], factory.LastSocket.SentPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task SendAsync_OtherSocketErrors_StillReachTheCaller()
    {
        var factory = new MockUdpSocketFactory();
        await using var listener = new UdpListener(5000, factory, new Settings());
        await listener.StartAsync(TestContext.Current.CancellationToken);
        factory.LastSocket.SendError = SocketError.MessageSize;

        await Assert.ThrowsAsync<SocketException>(() =>
            listener.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881), TestContext.Current.CancellationToken));
    }
}



