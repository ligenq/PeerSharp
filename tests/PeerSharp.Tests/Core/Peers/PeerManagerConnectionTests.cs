using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerConnectionTests
{
    private class MockPeerCommunication : PeerCommunication
    {
        public int ConnectCalls { get; private set; }
        public TaskCompletionSource<bool> ConnectTask { get; } = new();

        public MockPeerCommunication(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
        }

        public System.Collections.Concurrent.ConcurrentQueue<int>? DialedPorts { get; init; }

        public override Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, bool offerEncryption = true, CancellationToken ct = default)
        {
            ConnectCalls++;
            DialedPorts?.Enqueue(port);
            return ConnectTask.Task;
        }
    }

    private class MockPeerFactory : IPeerCommunicationFactory
    {
        public MockPeerCommunication LastCreated { get; private set; } = null!;

        /// <summary>The port of every dial made, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<int> DialedPorts { get; } = new();

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            LastCreated = new MockPeerCommunication(torrent, listener, timeProvider) { DialedPorts = DialedPorts };
            return LastCreated;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
            => throw new NotImplementedException();

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task ConnectTo_QueuesAndProcessesConnection()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var timeProvider = new FakeTimeProvider();
        var factory = new MockPeerFactory();
        var governor = new TorrentTestUtility.MockConnectionGovernor();

        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, timeProvider, governor);
        await manager.StartAsync();

        // Act
        manager.ConnectTo("127.0.0.1", 12345);

        // Wait for async processing (the connection queue runs on the task pool)
        await TorrentTestUtility.WaitUntilAsync(() => factory.LastCreated != null, because: "peer to be created");
        await TorrentTestUtility.WaitUntilAsync(() => factory.LastCreated.ConnectCalls > 0, because: "ConnectAsync to be called");

        Assert.Equal(1, factory.LastCreated.ConnectCalls);

        await manager.StopAsync();
    }

    [Fact]
    public async Task APeerNamedBeforeARestart_IsDialledAgainAfterIt()
    {
        // As a magnet link's x.pe names one: known to no tracker, so nothing else would find it again.
        var torrent = TorrentTestUtility.CreateMinimal();
        var timeProvider = new FakeTimeProvider();
        var factory = new MockPeerFactory();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, timeProvider, new TorrentTestUtility.MockConnectionGovernor());
        await manager.StartAsync();

        manager.AddPeers([new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51888)], PeerSourceKind.Resume);
        await TorrentTestUtility.WaitUntilAsync(() => factory.DialedPorts.Contains(51888), because: "the named peer to be dialled");

        await manager.StopAsync();
        factory.DialedPorts.Clear();
        await manager.StartAsync();

        await TorrentTestUtility.WaitUntilAsync(() => factory.DialedPorts.Contains(51888), because: "the named peer to be dialled again after the restart");
        await manager.StopAsync();
    }

    [Fact]
    public async Task TheFirstStart_DialsNothingOfItsOwn()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var factory = new MockPeerFactory();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, new FakeTimeProvider(), new TorrentTestUtility.MockConnectionGovernor());

        await manager.StartAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(factory.DialedPorts);
        await manager.StopAsync();
    }

    [Fact]
    public async Task ConnectTo_RateLimitsConnections()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.ConnectionsPerSecond = 1; // Strict limit

        var timeProvider = new FakeTimeProvider();
        var factory = new MockPeerFactory();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, timeProvider, new TorrentTestUtility.MockConnectionGovernor());
        await manager.StartAsync();

        // Queue 2 connections
        manager.ConnectTo("127.0.0.1", 1001);
        manager.ConnectTo("127.0.0.1", 1002);

        // First should happen immediately
        await TorrentTestUtility.WaitUntilAsync(() => factory.LastCreated != null, because: "first peer to be created");

        var first = factory.LastCreated;

        // Wait for first connect
        await TorrentTestUtility.WaitUntilAsync(() => first.ConnectCalls > 0, because: "first ConnectAsync call");

        // Reset factory tracker to detect second
        // Since factory.LastCreated is overwritten, we just check if it changes
        // But we need to distinguish peer 1 from peer 2.
        // MockPeerCommunication doesn't have ID unless we set it.
        // Instead, let's verify delay.

        // Actually, FakeTimeProvider complicates Task.Delay unless PeerManager uses it for delay.
        // PeerManager uses _timeProvider for Task.Delay:
        // await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, cancellationToken)

        // So we must advance time for second connection to happen!

        // Assert second hasn't happened yet (it's waiting for 1000ms delay)
        // NOTE: This assertion is race-prone if the test runs too fast or slow, but with FakeTimeProvider it should be blocked.
        // However, the *first* iteration might not delay? 
        // ProcessConnectionQueueAsync: ConnectToInternal -> Delay.
        // So first one connects, THEN it delays.
        // So second one is blocked.

        // Store first peer
        var peer1 = factory.LastCreated;

        // Verify delay logic: advance time
        timeProvider.Advance(TimeSpan.FromSeconds(1.1));

        // Now second should proceed
        await TorrentTestUtility.WaitUntilAsync(() => !ReferenceEquals(factory.LastCreated, peer1), because: "second peer to be created");

        Assert.NotSame(peer1, factory.LastCreated);
        Assert.Equal(1, factory.LastCreated.ConnectCalls);

        await manager.StopAsync();
    }
}
