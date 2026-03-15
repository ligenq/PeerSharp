using System.Net;
using System.Reflection;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Utp;

public class UtpSecurityTests
{
    [Fact(Timeout = 30000)]
    public async Task TestConnectionHijacking_ResetInjection()
    {
        var settings = new Settings();

        // 1. Setup Server on 51001
        await using var listenerServer = new UdpListener(51001, new UdpSocketFactory(), settings);
        await listenerServer.StartAsync();
        await using var mgrServer = new UtpManager(TimeProvider.System);
        mgrServer.Start(listenerServer);

        // 2. Setup Client on 51002
        await using var listenerClient = new UdpListener(51002, new UdpSocketFactory(), settings);
        await listenerClient.StartAsync();
        await using var mgrClient = new UtpManager(TimeProvider.System);
        mgrClient.Start(listenerClient);

        UtpStream? serverStream = null;
        var connectionTcs = new TaskCompletionSource<bool>();

        mgrServer.OnNewConnection = (stream) =>
        {
            serverStream = stream;
            connectionTcs.SetResult(true);
        };

        // 3. Connect Client to Server
        var clientStream = mgrClient.CreateStream(new IPEndPoint(IPAddress.Loopback, 51001));
        await clientStream.ConnectAsync();

        await Task.WhenAny(connectionTcs.Task, Task.Delay(1000));
        Assert.NotNull(serverStream);

        byte[] warmup = new byte[16];
        await clientStream.WriteAsync(warmup);

        // 4. Get Server's SendId (RST uses send id per libutp)
        var prop = typeof(UtpStream).GetProperty("ConnectionIdSend", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        ushort serverSendId = (ushort)prop!.GetValue(serverStream)!;

        // 5. Setup Attacker on 51003
        using var attackerClient = new System.Net.Sockets.UdpClient(51003);

        // Craft RESET packet
        byte[] packet = new byte[20];
        // Type = RESET (3), Version = 1 -> 0x31
        packet[0] = 0x31;
        packet[1] = 0; // Extension
        // ConnectionId = serverSendId
        packet[2] = (byte)(serverSendId >> 8);
        packet[3] = (byte)serverSendId;

        // Other fields can be zero for RESET, or valid if strict checks were in place.
        // SeqNr should probably match expected, but current implementation might not check strict seq on Reset?
        // Let's check implementation: 
        // case MessageType.ST_RESET: ... CloseInternal(false);
        // It doesn't seem to check SeqNr in current implementation! (Another bug/weakness, but hijack is primary).

        // Send to Server
        await attackerClient.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, 51001));

        // 6. Verify Server Stream State
        // Give it a moment to process
        await Task.Delay(100);

        // If bug exists, stream should be closed/disposed or in valid state?
        // CloseInternal calls _manager.CloseStream which removes it.
        // And _connectTcs set to exception.

        // We can check if writing to it throws or if it is in Closed state.
        // We can check private _state field.
        var stateField = typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        var state = stateField!.GetValue(serverStream);

        // Before Fix: State should be Closed (5) or similar, because attacker killed it.
        // After Fix: State should be Connected (3).

        // Assert that we SUCCESSFULLY protected it (fix verified)
        Assert.Equal("Connected", state!.ToString());
    }
}





