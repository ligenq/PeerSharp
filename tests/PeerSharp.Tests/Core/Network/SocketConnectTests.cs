using PeerSharp.Internals.Framework;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// Connecting to a peer that is not there, without paying for an exception.
/// </summary>
/// <remarks>
/// <para>
/// Most addresses a swarm hands out are not answering, so this path runs far more often than the one
/// that succeeds. The cost is not throughput - a thrown exception is under two microseconds - it is
/// that every first-chance exception is a round trip to an attached debugger, so a consumer stepping
/// through their own application pays, in visible sluggishness, for how often this engine dials a
/// dead peer.
/// </para>
/// <para>
/// The assertion counting exceptions is the point of the file. Everything else here would pass just
/// as well against the task-based connect that throws.
/// </para>
/// <para>
/// In the serialised collection because <see cref="AppDomain.FirstChanceException"/> is raised for
/// the whole process: run alongside the rest of the assembly, this counts other tests' exceptions
/// and fails for their reasons rather than its own.
/// </para>
/// </remarks>
[Collection("Concurrency")]
public class SocketConnectTests
{
    [Fact(Timeout = 30_000)]
    public async Task ConnectingToAListenerSucceeds()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        var error = await SocketConnect.ConnectAsync(
            socket, (IPEndPoint)listener.LocalEndPoint!, TestContext.Current.CancellationToken);

        Assert.Equal(SocketError.Success, error);
        Assert.True(socket.Connected);
    }

    [Fact(Timeout = 30_000)]
    public async Task DiallingPeersThatAreNotThereCostsNoExceptions()
    {
        // The assertion that matters, and the reason this file exists. Whether a dead address
        // refuses, is filtered, or is simply given up on differs by host and by firewall - what must
        // not differ is that none of them raises a first-chance exception.
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var closedPort = (IPEndPoint)listener.LocalEndPoint!;
        listener.Close();

        IPEndPoint[] targets =
        [
            closedPort,
            new(IPAddress.Parse("192.0.2.1"), 6881),
            new(IPAddress.Parse("198.51.100.1"), 51413)
        ];

        int thrown = 0;
        void Count(object? sender, FirstChanceExceptionEventArgs e) => Interlocked.Increment(ref thrown);

        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            foreach (var target in targets)
            {
                for (int i = 0; i < 5; i++)
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(300));

                    var error = await SocketConnect.ConnectAsync(socket, target, cts.Token);

                    Assert.NotEqual(SocketError.Success, error);
                }
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        Assert.Equal(0, thrown);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellingReportsAbortedRatherThanThrowing()
    {
        // An address that will not answer, cancelled before the OS gives up on its own. Reported,
        // because giving up on a dead peer is a decision this engine makes constantly.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = new CancellationTokenSource();

        var connect = SocketConnect.ConnectAsync(socket, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 6881), cts.Token);
        await cts.CancelAsync();

        var error = await connect;

        Assert.NotEqual(SocketError.Success, error);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAlreadyCancelledTokenNeverTouchesTheSocket()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var error = await SocketConnect.ConnectAsync(socket, new IPEndPoint(IPAddress.Loopback, 6881), cts.Token);

        Assert.Equal(SocketError.OperationAborted, error);
        Assert.False(socket.Connected);
    }
}
