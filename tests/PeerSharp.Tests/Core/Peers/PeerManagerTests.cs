using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerTests
{
    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_ForceProxy_Rejects()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Proxy.Type = ProxyType.Http;
        ctx.Torrent.Settings.Proxy.Host = "proxy";
        ctx.Torrent.Settings.Proxy.ForceProxy = true;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(0, ctx.Governor.AcquiredConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_Blocklist_Rejects()
    {
        var ctx = CreateContext();
        var blocklist = new IpBlocklist();
        blocklist.AddRange(IPAddress.Loopback, IPAddress.Loopback);
        blocklist.Enabled = true;
        ctx.Torrent.Blocklist = blocklist;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(ctx.Torrent.InfoFile.Info.Hash.Span, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(0, ctx.Governor.AcquiredConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_InvalidHandshake_ReleasesSlot()
    {
        var ctx = CreateContext();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] wrongHash = Enumerable.Repeat((byte)0xAB, 20).ToArray();
        byte[] handshake = BuildHandshake(wrongHash, ctx.Torrent.Settings.PeerId);
        await ctx.Manager.AddIncomingPeerAsync(serverClient, handshake);

        Assert.Equal(0, ctx.Manager.ConnectedCount);
        Assert.Equal(1, ctx.Governor.AcquiredConnections);
        Assert.Equal(1, ctx.Governor.ReleasedConnections);

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task ApplyConnectionBackoff_SetsNextAttemptWithExponentialDelay()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.PeerReconnectBaseSeconds = 5;
        ctx.Torrent.Settings.Connection.PeerReconnectMaxSeconds = 300;
        ctx.Torrent.Settings.Connection.PeerReconnectJitterMs = 0;

        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 12345) };
        history.FruitlessConnectionCount = 3; // 5 * 2^(3-1) = 20s

        InvokePrivate(ctx.Manager, "ApplyConnectionBackoff", history);

        var now = TimeProvider.System.GetUtcNow();
        Assert.True(history.NextConnectAttempt >= now.AddSeconds(18));
        Assert.True(history.NextConnectAttempt <= now.AddSeconds(25));

        await CleanupAsync(ctx);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPeerHealthAsync_DropsSlowPeerAfterGrace()
    {
        var ctx = CreateContext();
        ctx.Torrent.Settings.Connection.SlowPeerMinConnectedPeers = 1;
        ctx.Torrent.Settings.Connection.SlowPeerMinDownloadSpeedBytesPerSec = 1000;
        ctx.Torrent.Settings.Connection.SlowPeerGraceSeconds = 0;

        var peer = new PeerCommunication(ctx.Torrent, new TestPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_smoothedDownloadSpeed", 0);
        SetPrivateField(peer, "_amInterested", 1);
        SetPrivateField(peer, "_peerChoking", 0);
        SetPrivateField(peer, "_lastActivityTicksValue", Environment.TickCount64);

        var connectedPeers = GetPrivateField<ConcurrentDictionary<PeerCommunication, byte>>(ctx.Manager, "_connectedPeers");
        connectedPeers.TryAdd(peer, 0);
        SetPrivateField(ctx.Manager, "_connectedPeersCount", 1);

        var slowPeers = GetPrivateField<ConcurrentDictionary<PeerCommunication, long>>(ctx.Manager, "_slowPeers");
        slowPeers.TryAdd(peer, Environment.TickCount64 - 10_000);

        await (Task)InvokePrivate(ctx.Manager, "CheckPeerHealthAsync")!;

        Assert.False(slowPeers.ContainsKey(peer));

        await CleanupAsync(ctx);
    }

    private static PeerManagerContext CreateContext()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });

        string path = CreateTempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        torrent.Settings.Connection.Encryption = Encryption.Refuse;

        var governor = new FakeConnectionGovernor();
        var manager = new PeerManager(torrent, new FakeGeoIpService(), new RealPeerFactory(), TimeProvider.System, governor);

        return new PeerManagerContext(torrent, manager, governor, path);
    }

    private static byte[] BuildHandshake(ReadOnlySpan<byte> infoHash, byte[] peerId)
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        infoHash.CopyTo(handshake.AsSpan(28, 20));
        peerId.CopyTo(handshake, 48);
        return handshake;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerManager", Guid.NewGuid().ToString("N"));
    }

    private static async Task CleanupAsync(PeerManagerContext ctx)
    {
        await ctx.Manager.StopAsync();
        await ctx.Torrent.DisposeAsync();
        try
        {
            if (Directory.Exists(ctx.Path))
            {
                Directory.Delete(ctx.Path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private sealed record PeerManagerContext(Torrent Torrent, PeerManager Manager, FakeConnectionGovernor Governor, string Path);

    private static object? InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found on {target.GetType().Name}");
        }
        return method.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string fieldName) where T : class
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        }
        return (T)field.GetValue(target)!;
    }

    private sealed class FakeGeoIpService : IGeoIpService
    {
        public bool Enabled { get; set; }
        public string GetCountry(IPAddress ip) => "US";
        public void Load(Stream stream) { Enabled = true; }
        public Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            Enabled = true;
            return Task.CompletedTask;
        }
        public void Clear() { Enabled = false; }
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    private sealed class RealPeerFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }
    }

    private sealed class FakeConnectionGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public int AcquiredConnections { get; private set; }
        public int ReleasedConnections { get; private set; }

        public bool TryAcquireConnectionSlot()
        {
            AcquiredConnections++;
            return true;
        }

        public bool TryAcquirePendingSlot() => true;
        public void ReleaseConnectionSlot() => ReleasedConnections++;
        public void ReleasePendingSlot() { }
    }
}




