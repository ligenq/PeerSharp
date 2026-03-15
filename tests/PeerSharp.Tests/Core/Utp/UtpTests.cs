using System.Net;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Utp;

public class UtpTests
{
    [Fact(Timeout = 30000)]
    public async Task TestUtpConnectionAndTransfer()
    {
        var settings = new Settings();

        // Setup Listener 1
        var socketFactory = new UdpSocketFactory();
        var listener1 = new UdpListener(50001, socketFactory, settings);
        await listener1.StartAsync();
        var mgr1 = new UtpManager(TimeProvider.System);
        mgr1.Start(listener1);

        // Setup Listener 2
        var listener2 = new UdpListener(50002, socketFactory, settings);
        await listener2.StartAsync();
        var mgr2 = new UtpManager(TimeProvider.System);
        mgr2.Start(listener2);

        UtpStream? serverSideStream = null;
        var connectionTcs = new TaskCompletionSource<bool>();

        mgr2.OnNewConnection = (stream) =>
        {
            serverSideStream = stream;
            connectionTcs.SetResult(true);
        };

        // Connect 1 -> 2
        var clientStream = mgr1.CreateStream(new IPEndPoint(IPAddress.Loopback, 50002));
        await clientStream.ConnectAsync();

        // Wait for server to accept
        await Task.WhenAny(connectionTcs.Task, Task.Delay(1000));
        Assert.True(serverSideStream != null, "Server did not receive connection");

        // Send Data 1 -> 2
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        await clientStream.WriteAsync(data);

        // Read Data 2
        byte[] buffer = new byte[10];
        int read = await serverSideStream!.ReadAsync(buffer.AsMemory(0, 10));

        Assert.Equal(5, read);
        Assert.Equal(1, buffer[0]);
        Assert.Equal(5, buffer[4]);

        // Cleanup
        clientStream.Close();
        serverSideStream.Close();
        mgr1.Stop();
        mgr2.Stop();
        await listener1.DisposeAsync();
        await listener2.DisposeAsync();
    }
}





