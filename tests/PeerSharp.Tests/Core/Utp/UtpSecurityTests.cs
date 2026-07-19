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
        var socketFactory = new UdpSocketFactory();

        // Bind both listeners (and the attacker socket) on ephemeral ports. Hard-coded ports
        // collide with Windows' reserved/excluded UDP ranges (e.g. 50982-51081), which causes
        // bind() to fail with WSAEACCES on some hosts.
        await using var listenerServer = new UdpListener(0, socketFactory, settings);
        await listenerServer.StartAsync();
        await using var mgrServer = new UtpManager(TimeProvider.System);
        mgrServer.Start(listenerServer);
        int serverPort = listenerServer.Port;

        await using var listenerClient = new UdpListener(0, socketFactory, settings);
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

        var clientStream = mgrClient.CreateStream(new IPEndPoint(IPAddress.Loopback, serverPort));
        await clientStream.ConnectAsync();

        await Task.WhenAny(connectionTcs.Task, Task.Delay(1000));
        Assert.NotNull(serverStream);

        byte[] warmup = new byte[16];
        await clientStream.WriteAsync(warmup);

        // The client's ConnectAsync only guarantees the *client* is Connected (it received the
        // server's SYN-ACK). The server transitions SynRecv -> Connected only once it processes
        // the client's follow-up (the warmup DATA above). Wait for that before injecting the RST,
        // otherwise the assertion can race the handshake and observe SynRecv on a loaded host.
        var stateField = typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await WaitForStateAsync(serverStream, stateField, "Connected", TimeSpan.FromSeconds(5));

        // RST is matched on send id per libutp.
        var prop = typeof(UtpStream).GetProperty("ConnectionIdSend", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        ushort serverSendId = (ushort)prop!.GetValue(serverStream)!;

        using var attackerClient = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] packet = new byte[20];
        packet[0] = 0x31; // type=ST_RESET (3), version=1
        packet[1] = 0;    // extension
        packet[2] = (byte)(serverSendId >> 8);
        packet[3] = (byte)serverSendId;

        await attackerClient.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, serverPort));

        await Task.Delay(100);

        // Server stream must remain Connected: a RST from a spoofed source endpoint must not
        // tear down the legitimate connection.
        var state = stateField.GetValue(serverStream);
        Assert.Equal("Connected", state!.ToString());
    }

    private static async Task WaitForStateAsync(UtpStream stream, FieldInfo stateField, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (stateField.GetValue(stream)!.ToString() == expected)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Equal(expected, stateField.GetValue(stream)!.ToString());
    }
}





