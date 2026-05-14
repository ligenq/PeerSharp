using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class UdpListenerTests
{
    private class MockUdpSocket : IUdpSocket
    {
        public List<byte[]> SentPackets { get; } = [];
        private readonly System.Threading.Channels.Channel<UdpReceiveResult> _receiveChannel =
            System.Threading.Channels.Channel.CreateUnbounded<UdpReceiveResult>();

        public Socket Client { get; } = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public void EnqueueReceive(byte[] data, IPEndPoint remote)
        {
            _receiveChannel.Writer.TryWrite(new UdpReceiveResult(data, remote));
        }

        public void JoinMulticastGroup(IPAddress multicastAddr) { }
        public void Close() { }
        public void Dispose()
        {
            Client.Dispose();
        }

        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            return await _receiveChannel.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
        {
            SentPackets.Add(datagram.ToArray());
            return new ValueTask<int>(datagram.Length);
        }
    }

    private class MockUdpSocketFactory : IUdpSocketFactory
    {
        public MockUdpSocket LastSocket { get; } = new();
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
}





