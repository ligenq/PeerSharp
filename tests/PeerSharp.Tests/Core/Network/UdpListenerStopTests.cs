using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// Shutting down the UDP listener.
/// </summary>
/// <remarks>
/// <para>
/// This socket carries DHT, uTP and local peer discovery at once, and its two loops keep running
/// until told otherwise. Stopping has to end both of them and release the socket; a stop that
/// returns while a loop is still reading leaves a receive pending on a disposed socket, and one that
/// never returns holds up shutdown.
/// </para>
/// <para>
/// The listener used to carry a synchronous <c>Stop</c> beside this one that nothing called; it was
/// removed rather than tested, and these assertions moved onto the method the engine actually uses.
/// </para>
/// </remarks>
public class UdpListenerStopTests
{
    [Fact(Timeout = 30_000)]
    public async Task StoppingEndsBothLoopsAndReleasesTheSocket()
    {
        var factory = new StopTestSocketFactory();
        var listener = new UdpListener(0, factory, new Settings());
        var receiver = new RecordingReceiver();
        listener.RegisterReceiver(receiver);

        await listener.StartAsync(TestContext.Current.CancellationToken);
        factory.Socket.EnqueueReceive([1, 2, 3], new IPEndPoint(IPAddress.Loopback, 6881));

        // Wait for the packet to make it through both loops, so the stop is interrupting a listener
        // that is genuinely running rather than one that never started.
        while (receiver.Received.Count == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await listener.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(factory.Socket.Closed, "the socket was left open after Stop");
    }

    [Fact(Timeout = 30_000)]
    public async Task StoppingReturnsEvenWhenAReceiveIgnoresCancellation()
    {
        // A socket implementation that does not honour the token is the case the timed waits exist
        // for. Stop must give up on the loops rather than block shutdown behind them.
        var factory = new StopTestSocketFactory(ignoreCancellation: true);
        var listener = new UdpListener(0, factory, new Settings());
        listener.RegisterReceiver(new RecordingReceiver());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        await listener.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(factory.Socket.Closed);
    }

    [Fact(Timeout = 30_000)]
    public async Task StoppingTwiceIsHarmless()
    {
        // Shutdown paths overlap: the engine can stop a listener that a failed startup already
        // stopped, and the second call must not throw on the disposed socket.
        var factory = new StopTestSocketFactory();
        var listener = new UdpListener(0, factory, new Settings());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        await listener.StopAsync(TestContext.Current.CancellationToken);
        await listener.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(factory.Socket.Closed);
    }

    [Fact(Timeout = 30_000)]
    public async Task StoppingBeforeStartingIsHarmless()
    {
        // Nothing has been created yet, so there is nothing to wait on and nothing to close.
        var listener = new UdpListener(0, new StopTestSocketFactory(), new Settings());

        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task StoppingAfterDisposalStillCompletes()
    {
        // Shutdown paths overlap, so a stop can follow a disposal that already tore everything down.
        var factory = new StopTestSocketFactory();
        var listener = new UdpListener(0, factory, new Settings());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        await listener.DisposeAsync();
        await listener.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(factory.Socket.Closed);
    }

    private sealed class StopTestSocketFactory(bool ignoreCancellation = false) : IUdpSocketFactory
    {
        public StopTestSocket Socket { get; } = new(ignoreCancellation);

        public IUdpSocket Create(int port) => Socket;

        public IUdpSocket Create(AddressFamily family) => Socket;
    }

    private sealed class StopTestSocket(bool ignoreCancellation) : IUdpSocket
    {
        private readonly System.Threading.Channels.Channel<UdpReceiveResult> _receives =
            System.Threading.Channels.Channel.CreateUnbounded<UdpReceiveResult>();

        public bool Closed { get; private set; }

        public Socket Client { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public void EnqueueReceive(byte[] data, IPEndPoint remote)
            => _receives.Writer.TryWrite(new UdpReceiveResult(data, remote));

        public void Close() => Closed = true;

        public void Dispose()
        {
            Closed = true;
            Client.Dispose();
        }

        public void JoinMulticastGroup(IPAddress multicastAddr, IPAddress? localInterface = null)
        {
        }

        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            return await _receives.Reader.ReadAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
            => new(datagram.Length);
    }

    private sealed class RecordingReceiver : IUdpReceiver
    {
        public List<(byte[] Data, IPEndPoint Remote)> Received { get; } = [];

        public void Receive(byte[] data, IPEndPoint remote) => Received.Add((data, remote));
    }
}
