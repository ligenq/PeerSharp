using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Integration;

public sealed class UtpIntegrationTests
{
    private readonly Settings _settings = new Settings();
    private readonly UdpSocketFactory _socketFactory = new UdpSocketFactory();

    [Fact(Timeout = 30000)]
    public async Task ConnectAndTransfer_LargePayload_Succeeds()
    {
        await using var serverListener = await CreateListenerAsync();
        await using var clientListener = await CreateListenerAsync();
        await using var serverManager = CreateManager(serverListener);
        await using var clientManager = CreateManager(clientListener);

        var serverStreamTcs = new TaskCompletionSource<UtpStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverManager.OnNewConnection = stream => serverStreamTcs.TrySetResult(stream);

        var clientStream = clientManager.CreateStream(new IPEndPoint(IPAddress.Loopback, serverListener.Port));
        await clientStream.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var serverStream = await serverStreamTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        await clientStream.WriteAsync(payload);

        byte[] received = new byte[payload.Length];
        int offset = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (offset < received.Length)
        {
            int read = await serverStream.ReadAsync(received.AsMemory(offset), cts.Token);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }

        Assert.Equal(payload.Length, offset);
        Assert.Equal(payload, received);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionIdentity_AllowsSameConnIdDifferentRemotes()
    {
        await using var serverListener = await CreateListenerAsync();
        await using var serverManager = CreateManager(serverListener);

        var connections = new ConcurrentBag<UtpStream>();
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        serverManager.OnNewConnection = stream =>
        {
            connections.Add(stream);
            if (connections.Count >= 2)
            {
                connectedTcs.TrySetResult(true);
            }
        };

        ushort connId = 4242;
        byte[] synPacket = CreatePacket(MessageType.ST_SYN, connId, seq: 1, ack: 0);

        using var clientA = new UdpClient(0);
        using var clientB = new UdpClient(0);
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverListener.Port);

        await clientA.SendAsync(synPacket, serverEndpoint);
        await clientB.SendAsync(synPacket, serverEndpoint);

        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var streams = connections.ToArray();
        Assert.Equal(2, streams.Length);
        Assert.NotEqual(streams[0].RemoteEndPoint, streams[1].RemoteEndPoint);
    }

    [Fact(Timeout = 30000)]
    public async Task Reset_RequiresSendId()
    {
        await using var serverListener = await CreateListenerAsync();
        await using var clientListener = await CreateListenerAsync();
        await using var serverManager = CreateManager(serverListener);
        await using var clientManager = CreateManager(clientListener);

        var serverStreamTcs = new TaskCompletionSource<UtpStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverManager.OnNewConnection = stream => serverStreamTcs.TrySetResult(stream);

        var clientStream = clientManager.CreateStream(new IPEndPoint(IPAddress.Loopback, serverListener.Port));
        await clientStream.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var serverStream = await serverStreamTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] warmup = new byte[16];
        await clientStream.WriteAsync(warmup);

        ushort recvId = GetPrivatePropertyUShort(serverStream, "ConnectionIdRecv");
        ushort sendId = GetPrivatePropertyUShort(serverStream, "ConnectionIdSend");
        ushort ackNr = (ushort)(GetPrivateUShort(serverStream, "_seqNr") - 1);

        byte[] resetWrong = CreatePacket(MessageType.ST_RESET, recvId, seq: 0, ack: ackNr);
        await clientListener.SendAsync(resetWrong, new IPEndPoint(IPAddress.Loopback, serverListener.Port), CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal("Connected", GetPrivateEnum(serverStream, "_state"));

        byte[] resetRight = CreatePacket(MessageType.ST_RESET, sendId, seq: 0, ack: ackNr);
        await clientListener.SendAsync(resetRight, new IPEndPoint(IPAddress.Loopback, serverListener.Port), CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal("Closed", GetPrivateEnum(serverStream, "_state"));
    }

    private static ushort GetPrivatePropertyUShort(object obj, string propertyName)
    {
        var p = obj.GetType().GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return (ushort)p!.GetValue(obj)!;
    }

    private async Task<UdpListener> CreateListenerAsync()
    {
        var listener = new UdpListener(0, _socketFactory, _settings);
        await listener.StartAsync();
        return listener;
    }

    private static UtpManager CreateManager(UdpListener listener)
    {
        var manager = new UtpManager(TimeProvider.System);
        manager.Start(listener);
        return manager;
    }

    private static byte[] CreatePacket(MessageType type, ushort connId, ushort seq, ushort ack)
    {
        byte[] buffer = new byte[20];
        buffer[0] = (byte)(((byte)type << 4) | 1);
        buffer[1] = 0;
        UtpManager.WriteUInt16BigEndian(buffer, 2, connId);
        UtpManager.WriteUInt32BigEndian(buffer, 4, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 8, 0);
        UtpManager.WriteUInt32BigEndian(buffer, 12, 65535);
        UtpManager.WriteUInt16BigEndian(buffer, 16, seq);
        UtpManager.WriteUInt16BigEndian(buffer, 18, ack);
        return buffer;
    }

    private static ushort GetPrivateUShort(UtpStream stream, string fieldName)
    {
        var f = typeof(UtpStream).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return (ushort)f!.GetValue(stream)!;
    }

    private static string GetPrivateEnum(UtpStream stream, string fieldName)
    {
        var f = typeof(UtpStream).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return f!.GetValue(stream)!.ToString()!;
    }

}





