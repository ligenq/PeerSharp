using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// The engine starting on a host that will not give it the port it asked for.
/// </summary>
/// <remarks>
/// <para>
/// This is the failure a user reported against 3.2.0, and its stack was exactly this call:
/// <c>ClientEngine.InitializeAsync</c> to <c>NetworkManager.StartAsync</c> to
/// <c>UdpListener.StartAsync</c> to a bind that threw
/// <see cref="SocketError.AccessDenied"/> - WSAEACCES, "an attempt was made to access a socket in a
/// way forbidden by its access permissions". Nothing was listening on the port. Windows reserves
/// blocks of the dynamic range for Hyper-V, WSL and Docker, and a bind inside one fails that way;
/// the blocks are assigned at boot and move, so the port that worked yesterday can be refused today.
/// </para>
/// <para>
/// The binder has its own tests, but they drive it through a stand-in bind function. This drives the
/// listener, because what failed for that user was the listener's own wiring: the whole engine
/// refused to start over one unavailable UDP port.
/// </para>
/// </remarks>
public class UdpListenerBindFallbackTests
{
    [Fact(Timeout = 30_000)]
    public async Task AReservedPortIsSteppedOverRatherThanFatal()
    {
        // The reported case: the configured port is refused outright, with nothing listening on it.
        var factory = new RefusingSocketFactory(refuseUpTo: 6881);
        var listener = new UdpListener(6881, factory, new Settings());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6882, factory.BoundPort);
        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWholeReservedBlockFallsBackToAnOsAssignedPort()
    {
        // Windows reserves ranges, not single ports, so the next port along is usually refused too.
        // Past the binder's run of retries the listener has to take whatever the OS will give it -
        // an engine on an unexpected port still finds peers, one that will not start finds nothing.
        var factory = new RefusingSocketFactory(refuseUpTo: 60000);
        var listener = new UdpListener(55125, factory, new Settings());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, factory.BoundPort);
        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task APortAlreadyInUseIsSteppedOverTheSameWay()
    {
        // A second instance of the caller's own application is the other common cause, and reports a
        // different error for the same situation.
        var factory = new RefusingSocketFactory(refuseUpTo: 6881, error: SocketError.AddressAlreadyInUse);
        var listener = new UdpListener(6881, factory, new Settings());

        await listener.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6882, factory.BoundPort);
        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailureThatIsNotAboutThePortStillSurfaces()
    {
        // Walking to the next port cannot fix a broken network stack, and quietly binding somewhere
        // else would hide it.
        var factory = new RefusingSocketFactory(refuseUpTo: int.MaxValue, error: SocketError.NetworkDown);
        var listener = new UdpListener(6881, factory, new Settings());

        var thrown = await Assert.ThrowsAsync<SocketException>(
            () => listener.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SocketError.NetworkDown, thrown.SocketErrorCode);
    }

    /// <summary>
    /// Refuses every port up to and including <c>refuseUpTo</c>, the way a reserved range does.
    /// </summary>
    private sealed class RefusingSocketFactory(int refuseUpTo, SocketError error = SocketError.AccessDenied)
        : IUdpSocketFactory
    {
        public int? BoundPort { get; private set; }

        public IUdpSocket Create(int port)
        {
            // Port 0 is the OS picking one, which a reservation cannot refuse.
            if (port != 0 && port <= refuseUpTo)
            {
                throw new SocketException((int)error);
            }

            BoundPort = port;
            return new StubSocket();
        }

        public IUdpSocket Create(AddressFamily family) => new StubSocket();
    }

    private sealed class StubSocket : IUdpSocket
    {
        private readonly TaskCompletionSource<UdpReceiveResult> _never = new();

        public Socket Client { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public void Close()
        {
        }

        public void Dispose() => Client.Dispose();

        public void JoinMulticastGroup(IPAddress multicastAddr, IPAddress? localInterface = null)
        {
        }

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
            => _never.Task.WaitAsync(cancellationToken);

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
            => new(datagram.Length);
    }
}
