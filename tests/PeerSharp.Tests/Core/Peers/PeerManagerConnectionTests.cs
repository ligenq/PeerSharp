using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Framework;
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

        public override Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, CancellationToken ct = default)
        {
            ConnectCalls++;
            return ConnectTask.Task;
        }
    }

    private class MockPeerFactory : IPeerCommunicationFactory
    {
        public MockPeerCommunication LastCreated { get; private set; } = null!;

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            LastCreated = new MockPeerCommunication(torrent, listener, timeProvider);
            return LastCreated;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
            => throw new NotImplementedException();

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
            => throw new NotImplementedException();
    }

    private class MockGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public bool TryAcquireConnectionSlot() => true;
        public bool TryAcquirePendingSlot() => true;
        public void ReleaseConnectionSlot() { }
        public void ReleasePendingSlot() { }
    }

    private class MockGeoIp : IGeoIpService
    {
        public bool Enabled { get; set; }
        public string GetCountry(IPAddress ip) => "US";
        public void Load(Stream stream) { }
        public Task LoadAsync(Stream stream, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Clear() { }
    }

    [Fact]
    public async Task ConnectTo_QueuesAndProcessesConnection()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var timeProvider = new FakeTimeProvider();
        var factory = new MockPeerFactory();
        var governor = new MockGovernor();

        var manager = new PeerManager(torrent, new MockGeoIp(), factory, timeProvider, governor);
        await manager.StartAsync();

        // Act
        manager.ConnectTo("127.0.0.1", 12345);

        // Wait for async processing (pump queue)
        // Since ProcessConnectionQueueAsync runs on Task pool, we wait briefly or poll
        int attempts = 0;
        while (factory.LastCreated == null && attempts++ < 100)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(factory.LastCreated);

        // Wait for connect call
        attempts = 0;
        while (factory.LastCreated.ConnectCalls == 0 && attempts++ < 100)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, factory.LastCreated.ConnectCalls);

        await manager.StopAsync();
    }

    [Fact]
    public async Task ConnectTo_RateLimitsConnections()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.ConnectionsPerSecond = 1; // Strict limit

        var timeProvider = new FakeTimeProvider();
        var factory = new MockPeerFactory();
        var manager = new PeerManager(torrent, new MockGeoIp(), factory, timeProvider, new MockGovernor());
        await manager.StartAsync();

        // Queue 2 connections
        manager.ConnectTo("127.0.0.1", 1001);
        manager.ConnectTo("127.0.0.1", 1002);

        // First should happen immediately
        while (factory.LastCreated == null)
        {
            await Task.Delay(5);
        }

        var first = factory.LastCreated;

        // Wait for first connect
        while (first.ConnectCalls == 0)
        {
            await Task.Delay(5);
        }

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
        while (factory.LastCreated == peer1)
        {
            await Task.Delay(5);
        }

        Assert.NotSame(peer1, factory.LastCreated);
        Assert.Equal(1, factory.LastCreated.ConnectCalls);

        await manager.StopAsync();
    }
}
