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
        byte[] data = [1, 2, 3, 4, 5];
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

    [Fact(Timeout = 30000)]
    public async Task WriteAsync_FillsWindow_BlocksUntilAcked()
    {
        var settings = new Settings();
        var socketFactory = new UdpSocketFactory();
        var listener1 = new UdpListener(50011, socketFactory, settings);
        await listener1.StartAsync();
        var mgr1 = new UtpManager(TimeProvider.System);
        mgr1.Start(listener1);

        var listener2 = new UdpListener(50012, socketFactory, settings);
        await listener2.StartAsync();
        var mgr2 = new UtpManager(TimeProvider.System);
        mgr2.Start(listener2);

        UtpStream? serverSideStream = null;
        var connectionTcs = new TaskCompletionSource<bool>();
        mgr2.OnNewConnection = (stream) => { serverSideStream = stream; connectionTcs.SetResult(true); };

        var clientStream = mgr1.CreateStream(new IPEndPoint(IPAddress.Loopback, 50012));
        await clientStream.ConnectAsync();
        await connectionTcs.Task;

        var cwndField = typeof(UtpStream).GetField("_cwnd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(cwndField);
        cwndField.SetValue(clientStream, 1000.0); // 1000 bytes window

        // Stop the server from receiving packets and sending ACKs
        await listener2.StopAsync();

        byte[] largeData = new byte[3000];
        var writeTask = clientStream.WriteAsync(largeData).AsTask();

        // Ensure it blocks because window fills up and no ACKs are received
        var delayTask = Task.Delay(500);
        var completedFirst = await Task.WhenAny(writeTask, delayTask);
        Assert.Equal(delayTask, completedFirst); // Delay should complete first, meaning WriteAsync is blocked

        clientStream.Dispose();
        serverSideStream!.Close();
        mgr1.Stop();
        mgr2.Stop();
        await listener1.DisposeAsync();
        await listener2.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_CancelsPendingWriteAsync()
    {
        var settings = new Settings();
        var socketFactory = new UdpSocketFactory();
        var listener1 = new UdpListener(50021, socketFactory, settings);
        await listener1.StartAsync();
        var mgr1 = new UtpManager(TimeProvider.System);
        mgr1.Start(listener1);

        var listener2 = new UdpListener(50022, socketFactory, settings);
        await listener2.StartAsync();
        var mgr2 = new UtpManager(TimeProvider.System);
        mgr2.Start(listener2);

        UtpStream? serverSideStream = null;
        var connectionTcs = new TaskCompletionSource<bool>();
        mgr2.OnNewConnection = (stream) => { serverSideStream = stream; connectionTcs.SetResult(true); };

        var clientStream = mgr1.CreateStream(new IPEndPoint(IPAddress.Loopback, 50022));
        await clientStream.ConnectAsync();
        await connectionTcs.Task;

        var cwndField = typeof(UtpStream).GetField("_cwnd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(cwndField);
        cwndField.SetValue(clientStream, 1000.0);

        await listener2.StopAsync();

        byte[] largeData = new byte[3000];
        var writeTask = clientStream.WriteAsync(largeData).AsTask();

        await Task.Delay(100); // Allow it to get blocked

        clientStream.Dispose();

        // WaitAsync on Semaphore throws ObjectDisposedException
        await Assert.ThrowsAnyAsync<Exception>(() => writeTask);

        serverSideStream!.Close();
        mgr1.Stop();
        mgr2.Stop();
        await listener1.DisposeAsync();
        await listener2.DisposeAsync();
    }
}





