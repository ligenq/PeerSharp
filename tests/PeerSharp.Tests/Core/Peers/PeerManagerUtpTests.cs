using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utp;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerUtpTests
{
    private class FakeUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void Start(IUdpListener listener) { }
        public void Stop() { }
        public UtpStream CreateStream(IPEndPoint remote) => null!;
        public void CloseStream(UtpStream stream) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class FakePeerCommunicationFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return new PeerCommunication(torrent, listener, timeProvider)
            {
                Stream = stream,
                RemoteEndPoint = remoteEndPoint,
                Connected = 1
            };
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }
    }

    private class FakePeerListener : IPeerListener
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

    private static PeerManager CreateManager(ConnectionSettings settings, FakeTimeProvider timeProvider)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection = settings;
        torrent.UtpManager = new FakeUtpManager();
        return new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new FakePeerCommunicationFactory(), timeProvider, new TorrentTestUtility.MockConnectionGovernor());
    }

    private static List<string> GetTransportPlan(PeerManager manager, ConnectionSettings settings, PeerHistory? history)
    {
        var method = typeof(PeerManager).GetMethod("BuildTransportPlan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var plan = (System.Collections.IEnumerable)method!.Invoke(manager, [settings, history, false])!;
        return plan.Cast<object>().Select(p => p.ToString() ?? string.Empty).ToList();
    }

    private static void AddConnectedUtpPeer(PeerManager manager, Torrent torrent, TimeProvider timeProvider)
    {
        var listener = new FakePeerListener();
        var peer = new PeerCommunication(torrent, listener, timeProvider)
        {
            UtpStream = new UtpStream(new FakeUtpManager(), new IPEndPoint(IPAddress.Loopback, 1), 1, 2, timeProvider)
        };

        manager.AddConnectedPeerForTesting(peer);
    }

    [Fact]
    public void BuildTransportPlan_WarmupDisablesUtpForNonHintedPeers()
    {
        var timeProvider = new FakeTimeProvider();
        var settings = new ConnectionSettings
        {
            PreferUtp = true,
            EnableUtpOut = true,
            EnableTcpOut = true,
            UtpWarmupSeconds = 30
        };
        var manager = CreateManager(settings, timeProvider);
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpHinted = false };

        var plan = GetTransportPlan(manager, settings, history);

        Assert.Equal(new[] { "Tcp" }, plan);
    }

    [Fact]
    public void BuildTransportPlan_PrefersTcpThenUtpForUnknownAfterWarmup()
    {
        var timeProvider = new FakeTimeProvider();
        var settings = new ConnectionSettings
        {
            PreferUtp = true,
            EnableUtpOut = true,
            EnableTcpOut = true,
            UtpWarmupSeconds = 10
        };
        var manager = CreateManager(settings, timeProvider);
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpHinted = false };

        timeProvider.Advance(TimeSpan.FromSeconds(20));
        var plan = GetTransportPlan(manager, settings, history);

        Assert.Equal(new[] { "Tcp", "Utp" }, plan);
    }

    [Fact]
    public void BuildTransportPlan_PrefersUtpWhenBelowTargetRatio()
    {
        var timeProvider = new FakeTimeProvider();
        var settings = new ConnectionSettings
        {
            PreferUtp = true,
            EnableUtpOut = true,
            EnableTcpOut = true,
            PreferUtpRatioPercent = 70,
            UtpWarmupSeconds = 0
        };
        var manager = CreateManager(settings, timeProvider);
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpHinted = true };

        var plan = GetTransportPlan(manager, settings, history);

        Assert.Equal(new[] { "Utp", "Tcp" }, plan);
    }

    [Fact]
    public void BuildTransportPlan_UsesTcpWhenAboveTargetRatio()
    {
        var timeProvider = new FakeTimeProvider();
        var settings = new ConnectionSettings
        {
            PreferUtp = true,
            EnableUtpOut = true,
            EnableTcpOut = true,
            PreferUtpRatioPercent = 50,
            UtpWarmupSeconds = 0
        };
        var manager = CreateManager(settings, timeProvider);
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpHinted = true };

        AddConnectedUtpPeer(manager, TorrentTestUtility.CreateMinimal(), timeProvider);
        var plan = GetTransportPlan(manager, settings, history);

        Assert.Equal(new[] { "Tcp", "Utp" }, plan);
    }

    [Fact]
    public void BuildTransportPlan_DisablesUtpDuringGlobalPenalty()
    {
        var timeProvider = new FakeTimeProvider();
        var settings = new ConnectionSettings
        {
            PreferUtp = true,
            EnableUtpOut = true,
            EnableTcpOut = true,
            UtpWarmupSeconds = 0
        };
        var manager = CreateManager(settings, timeProvider);
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpHinted = true };

        manager.SetGlobalUtpPenaltyForTesting(timeProvider.GetUtcNow().AddMinutes(1));

        var plan = GetTransportPlan(manager, settings, history);

        Assert.Equal(new[] { "Tcp" }, plan);
    }

    [Fact]
    public void ApplyPexFlags_SetsSeedAndUtpHints()
    {
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881), UtpSupported = false, UtpHinted = false };
        var method = typeof(PeerManager).GetMethod("ApplyPexFlags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, [history, (byte)(UtPex.Peer.Seed | UtPex.Peer.Utp)]);

        Assert.True(history.IsSeed);
        Assert.True(history.UtpSupported);
        Assert.True(history.UtpHinted);
    }

    /// <summary>
    /// A stream that blocks indefinitely on Read and Write, preventing
    /// the background handshake loop from completing or failing.
    /// </summary>
    private class NeverCompleteStream : Stream
    {
        private readonly SemaphoreSlim _gate = new(0);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Silently accept writes (handshake, bitfield, etc.)
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public void Unblock() => _gate.Release(10);
    }

    [Fact]
    public async Task AddIncomingPeerAsync_Stream_AcceptsConnection()
    {
        var timeProvider = new FakeTimeProvider();
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection = new ConnectionSettings { EnableUtpIn = true };
        torrent.UtpManager = new FakeUtpManager();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new FakePeerCommunicationFactory(), timeProvider, new TorrentTestUtility.MockConnectionGovernor());

        var stream = new NeverCompleteStream();
        var handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        torrent.InfoFile.Info.Hash.CopyTo(handshake.AsSpan(28));
        torrent.Settings.PeerId.CopyTo(handshake, 48);

        await manager.AddIncomingPeerAsync(stream, handshake, new IPEndPoint(IPAddress.Loopback, 12345));

        // Allow time for the background handshake loop to process
        await Task.Delay(100);

        Assert.Equal(1, manager.ConnectedCount);

        stream.Unblock();
    }
}





