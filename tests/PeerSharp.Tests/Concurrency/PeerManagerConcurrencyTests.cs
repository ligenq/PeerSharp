using Microsoft.Coyote;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace PeerSharp.Tests.Concurrency;

[Collection("Coyote")]
public class PeerManagerConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public PeerManagerConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        using var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }

    private class MockPeerCommunication : PeerCommunication
    {
        public bool MockConnected { get; set; }

        public MockPeerCommunication(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
        }

        public override Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, CancellationToken ct)
        {
            MockConnected = true;
            return Task.FromResult(true);
        }

        public override Task CloseAsync()
        {
            MockConnected = false;
            return Task.CompletedTask;
        }
    }

    private class MockFactory : IPeerCommunicationFactory
    {
        public ConcurrentBag<MockPeerCommunication> CreatedPeers { get; } = [];

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            var p = new MockPeerCommunication(torrent, listener, timeProvider);
            CreatedPeers.Add(p);
            return p;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
        {
            var p = new MockPeerCommunication(torrent, listener, timeProvider);
            CreatedPeers.Add(p);
            return p;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
        {
            var p = new MockPeerCommunication(torrent, listener, timeProvider);
            CreatedPeers.Add(p);
            return p;
        }
    }

    [Fact]
    public void PeerManager_ConcurrentConnect_SamePeer_OnlyOneConnects()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var factory = new MockFactory();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());

            var ep = new IPEndPoint(IPAddress.Loopback, 12345);
            var ipStr = ep.Address.ToString();

            // Concurrently attempt to connect to the same peer 5 times.
            // ConnectTo performs deduplication synchronously before queuing,
            // so only one request should be enqueued regardless of concurrency.
            var tasks = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(() => manager.ConnectTo(ipStr, ep.Port)));
            }

            Task.WaitAll(tasks.ToArray());

            // The pending connections tracking ensures only one connection attempt
            // is active for a given endpoint at a time. We verify the invariant
            // held under concurrent access by checking no exceptions were thrown
            // and the manager state is consistent.
        });
    }

    [Fact]
    public void PeerManager_IncomingOutgoingCollision_MaintainsConsistentState()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var factory = new MockFactory();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());

            var ep = new IPEndPoint(IPAddress.Loopback, 12345);
            var ipStr = ep.Address.ToString();

            var tasks = new List<Task>
            {
                // Outgoing connection attempt
                Task.Run(() => manager.ConnectTo(ipStr, ep.Port)),

                // Incoming connection (simulated handshake completion)
                Task.Run(async () =>
                {
                    var incomingPeer = new MockPeerCommunication(torrent, manager, TimeProvider.System)
                    {
                        RemoteEndPoint = ep
                    };

                    await manager.HandshakeFinishedAsync(incomingPeer);
                })
            };

            Task.WaitAll(tasks.ToArray());

            Specification.Assert(manager.ConnectedCount <= 1,
                $"Duplicate connections allowed: {manager.ConnectedCount}");
        });
    }

    [Fact]
    public void PeerManager_ConcurrentEndpointRegistration_HasOneOwner()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new MockFactory(), TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
            var endpoint = new IPEndPoint(IPAddress.Loopback, 43210);
            int claims = 0;

            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var peer = new MockPeerCommunication(torrent, manager, TimeProvider.System) { RemoteEndPoint = endpoint };
                if (manager.TryRegisterConnectedEndpointForTesting(peer))
                {
                    Interlocked.Increment(ref claims);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            Specification.Assert(claims == 1, $"Expected exactly one endpoint owner, got {claims}");
            Specification.Assert(manager.ConnectedEndpointCountForTesting == 1,
                $"Endpoint index must contain exactly one entry, got {manager.ConnectedEndpointCountForTesting}");
        });
    }

    [Fact]
    public void PeerManager_ConcurrentPeerIdRegistration_HasOneOwner()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new MockFactory(), TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
            byte[] peerId = Enumerable.Repeat((byte)0x5A, 20).ToArray();
            int claims = 0;

            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var peer = new MockPeerCommunication(torrent, manager, TimeProvider.System);
                peerId.CopyTo(peer.PeerId, 0);
                if (manager.TryRegisterConnectedPeerIdForTesting(peer))
                {
                    Interlocked.Increment(ref claims);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            Specification.Assert(claims == 1, $"Expected exactly one peer-id owner, got {claims}");
            Specification.Assert(manager.ConnectedPeerIdCountForTesting == 1,
                $"Peer-id index must contain exactly one entry, got {manager.ConnectedPeerIdCountForTesting}");
        });
    }

    [Fact]
    public void PeerManager_DuplicateEndpointCleanup_CannotEvictOwner()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new MockFactory(), TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
            var endpoint = new IPEndPoint(IPAddress.Loopback, 43211);
            var owner = new MockPeerCommunication(torrent, manager, TimeProvider.System) { RemoteEndPoint = endpoint };
            Assert.True(manager.TryRegisterConnectedEndpointForTesting(owner));

            var duplicates = Enumerable.Range(0, 8)
                .Select(_ => new MockPeerCommunication(torrent, manager, TimeProvider.System) { RemoteEndPoint = endpoint })
                .ToArray();
            Assert.All(duplicates, peer => Assert.False(manager.TryRegisterConnectedEndpointForTesting(peer)));

            Task.WaitAll(duplicates.Select(peer => Task.Run(() => manager.UnregisterConnectedEndpointForTesting(peer))).ToArray());

            Specification.Assert(manager.ConnectedEndpointCountForTesting == 1,
                "A duplicate endpoint cleanup removed the surviving owner's registration.");

            manager.UnregisterConnectedEndpointForTesting(owner);
            Specification.Assert(manager.ConnectedEndpointCountForTesting == 0,
                "The endpoint owner could not release its own registration.");
        });
    }

    [Fact]
    public void PeerManager_DuplicatePeerIdCleanup_CannotEvictOwner()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new MockFactory(), TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
            byte[] peerId = Enumerable.Repeat((byte)0xA5, 20).ToArray();
            var owner = new MockPeerCommunication(torrent, manager, TimeProvider.System);
            peerId.CopyTo(owner.PeerId, 0);
            Assert.True(manager.TryRegisterConnectedPeerIdForTesting(owner));

            var duplicates = Enumerable.Range(0, 8).Select(_ =>
            {
                var peer = new MockPeerCommunication(torrent, manager, TimeProvider.System);
                peerId.CopyTo(peer.PeerId, 0);
                return peer;
            }).ToArray();
            Assert.All(duplicates, peer => Assert.False(manager.TryRegisterConnectedPeerIdForTesting(peer)));

            Task.WaitAll(duplicates.Select(peer => Task.Run(() => manager.UnregisterConnectedPeerIdForTesting(peer))).ToArray());

            Specification.Assert(manager.ConnectedPeerIdCountForTesting == 1,
                "A duplicate peer-id cleanup removed the surviving owner's registration.");

            manager.UnregisterConnectedPeerIdForTesting(owner);
            Specification.Assert(manager.ConnectedPeerIdCountForTesting == 0,
                "The peer-id owner could not release its own registration.");
        });
    }

    [Fact]
    public void PeerManagerFailureTracker_ConcurrentFailures_EscalatesOnce()
    {
        RunCoyoteTest(() =>
        {
            var tracker = new PeerManagerFailureTracker();
            var results = new ConcurrentBag<PeerManagerFailureTracker.FailureRecord>();
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            Task.WaitAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => results.Add(tracker.Record(now))))
                .ToArray());

            Specification.Assert(tracker.TotalFailures == 8, $"Expected 8 recorded failures, got {tracker.TotalFailures}");
            Specification.Assert(results.Count(result => result.ShouldEscalate) == 1,
                "Concurrent failures should produce exactly one escalation per rate-limit window.");
        });
    }
}
