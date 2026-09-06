using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// What this client believes about a peer's uTP support, how it learns it, and how it withdraws it.
/// </summary>
public class UtpTransportPolicyTests
{
    private static ConnectionSettings Settings() => new()
    {
        UtpFailureHardLimit = 3,
        UtpPenaltyBaseSeconds = 60,
        UtpPenaltyMaxSeconds = 600,
        UtpSlowPenaltySeconds = 90,
        UtpSlowPenaltyCooldownSeconds = 60
    };

    private static PeerHistory History() => new()
    {
        EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881)
    };

    [Fact]
    public void APeerIsAssumedToSpeakUtpUntilSomethingSaysOtherwise()
    {
        // libtorrent's torrent_peer starts with supports_utp set and the comment "assume peers
        // support utp". Nothing is known about a peer at first, and guessing yes is what gets
        // LEDBAT used at all.
        var history = History();

        Assert.True(history.UtpSupported);
        Assert.True(history.IsUtpAllowed(DateTimeOffset.UnixEpoch));

        // Not the same as knowing: the guess has not been tested yet.
        Assert.False(history.UtpHinted);
        Assert.False(history.UtpConfirmed);
    }

    [Fact]
    public void ASuccessfulUtpConnectionTurnsTheGuessIntoKnowledge()
    {
        var history = History();
        var now = DateTimeOffset.UnixEpoch;

        history.RegisterUtpSuccess(now);

        Assert.True(history.UtpHinted);
        Assert.True(history.UtpConfirmed);
        Assert.True(history.UtpSupported);
        Assert.Equal(0, history.UtpFailureCount);
    }

    [Fact]
    public void RepeatedFailuresWithdrawAGuess()
    {
        // Three strikes and this client stops offering uTP to a peer it was only guessing about,
        // which is what keeps a UDP-blocked path from costing an attempt per peer forever.
        var history = History();
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;

        for (int i = 0; i < settings.UtpFailureHardLimit; i++)
        {
            history.RegisterUtpFailure(now, settings);
        }

        Assert.False(history.UtpSupported);
        Assert.False(history.IsUtpAllowed(now));
    }

    [Fact]
    public void RepeatedFailuresDoNotWithdrawWhatWasProved()
    {
        // The asymmetry libtorrent has: its demotion path checks the speculative flag only, so a
        // peer that has completed a uTP connection is not written off by a bad minute on the path.
        // A peer reachable only over uTP would otherwise be moved to TCP permanently and lost.
        var history = History();
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;
        history.RegisterUtpSuccess(now);

        for (int i = 0; i < settings.UtpFailureHardLimit * 3; i++)
        {
            history.RegisterUtpFailure(now, settings);
        }

        Assert.True(history.UtpSupported);
        Assert.True(history.UtpConfirmed);

        // It still serves out the backoff, so the failures are not free - just not fatal.
        Assert.False(history.IsUtpAllowed(now));
        Assert.True(history.IsUtpAllowed(now.AddSeconds(settings.UtpPenaltyMaxSeconds + 1)));
    }

    [Fact]
    public void TheSlowPenaltyFollowsTheSameRule()
    {
        // The other demotion path, reached when uTP is carrying traffic badly rather than failing
        // to connect. It has to agree with the one above or a confirmed peer is lost by the back
        // door.
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;

        var guessed = History();
        var confirmed = History();
        confirmed.RegisterUtpSuccess(now);

        for (int i = 0; i < settings.UtpFailureHardLimit; i++)
        {
            var at = now.AddSeconds(i * (settings.UtpSlowPenaltyCooldownSeconds + 1));
            guessed.RegisterUtpSlow(at, settings);
            confirmed.RegisterUtpSlow(at, settings);
        }

        Assert.False(guessed.UtpSupported);
        Assert.True(confirmed.UtpSupported);
    }

    [Fact]
    public void APenaltyExpiresRatherThanBanningThePeerOutright()
    {
        var history = History();
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;

        history.RegisterUtpFailure(now, settings);

        Assert.False(history.IsUtpAllowed(now));
        Assert.True(history.IsUtpAllowed(now.AddSeconds(settings.UtpPenaltyBaseSeconds + 1)));
    }

    [Fact]
    public void TheBackoffGrowsWithEachFailureAndIsCapped()
    {
        var history = History();
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;

        history.RegisterUtpFailure(now, settings);
        var afterFirst = history.UtpPenaltyUntil;

        history.RegisterUtpFailure(now, settings);
        Assert.True(history.UtpPenaltyUntil > afterFirst);

        for (int i = 0; i < 20; i++)
        {
            history.RegisterUtpFailure(now, settings);
        }

        Assert.True(history.UtpPenaltyUntil <= now.AddSeconds(settings.UtpPenaltyMaxSeconds));
    }

    [Fact]
    public void ASuccessClearsAPenaltyAlreadyInForce()
    {
        // Whatever the path was doing, it is doing it no longer.
        var history = History();
        var settings = Settings();
        var now = DateTimeOffset.UnixEpoch;
        history.RegisterUtpFailure(now, settings);
        Assert.False(history.IsUtpAllowed(now));

        history.RegisterUtpSuccess(now);

        Assert.True(history.IsUtpAllowed(now));
    }
}

public class UtpColdStartGuardTests
{
    [Fact]
    public void TheDefaultLimitIsSetAndTheSettingIsAdjustable()
    {
        // Leading with uTP is a guess about the network. This is how the guess is withdrawn for the
        // whole session rather than peer by peer, which is what a path that drops UDP needs.
        var settings = new ConnectionSettings();

        Assert.Equal(8, settings.UtpColdStartFailureLimit);

        settings.UtpColdStartFailureLimit = 0;
        Assert.Equal(0, settings.UtpColdStartFailureLimit);
    }

    [Fact]
    public async Task FailingUtpDialsWithNoSuccessEventuallyHoldUtpBackGlobally()
    {
        var clock = new FakeTimeProvider();
        var ctx = CreatePeerManager(clock, coldStartLimit: 3);
        try
        {
            var history = new PeerHistory
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881),
                UtpHinted = true
            };

            Assert.Equal(["Utp", "Tcp"], Plan(ctx.Manager, ctx.Torrent.Settings.Connection, history));

            for (int i = 0; i < 3; i++)
            {
                RecordColdStartFailure(ctx.Manager);
            }

            // The path is not carrying UDP, so uTP stops being offered rather than costing a capped
            // attempt on every peer in the swarm.
            Assert.Equal(["Tcp"], Plan(ctx.Manager, ctx.Torrent.Settings.Connection, history));
        }
        finally
        {
            await ctx.Manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task OneSuccessRetiresTheGuardForGood()
    {
        // The question it answers is whether uTP works here at all, and one answer settles it. A
        // later run of failures is the per-peer machinery's business, not this one's.
        var clock = new FakeTimeProvider();
        var ctx = CreatePeerManager(clock, coldStartLimit: 3);
        try
        {
            var history = new PeerHistory
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881),
                UtpHinted = true
            };

            RecordColdStartFailure(ctx.Manager);
            RecordUtpSuccess(ctx.Manager);

            for (int i = 0; i < 20; i++)
            {
                RecordColdStartFailure(ctx.Manager);
            }

            Assert.Equal(["Utp", "Tcp"], Plan(ctx.Manager, ctx.Torrent.Settings.Connection, history));
        }
        finally
        {
            await ctx.Manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task AZeroLimitTurnsTheGuardOff()
    {
        var clock = new FakeTimeProvider();
        var ctx = CreatePeerManager(clock, coldStartLimit: 0);
        try
        {
            var history = new PeerHistory
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881),
                UtpHinted = true
            };

            for (int i = 0; i < 50; i++)
            {
                RecordColdStartFailure(ctx.Manager);
            }

            Assert.Equal(["Utp", "Tcp"], Plan(ctx.Manager, ctx.Torrent.Settings.Connection, history));
        }
        finally
        {
            await ctx.Manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task FallbackTransportUsesAFreshPeerCommunication()
    {
        var clock = new FakeTimeProvider();
        var ctx = CreateFallbackManager(clock, coldStartLimit: 8, markUtpEstablished: false);
        try
        {
            await ctx.Manager.StartAsync();
            ctx.Manager.ConnectTo("127.0.0.1", 6881);

            await TorrentTestUtility.WaitUntilAsync(
                () => ctx.Manager.ConnectedCount == 1,
                because: "the TCP fallback to connect");

            var peers = ctx.Factory.Created.ToArray();
            Assert.Equal(2, peers.Length);
            Assert.NotSame(peers[0], peers[1]);
            Assert.Equal([true], peers[0].Transports);
            Assert.Equal([false], peers[1].Transports);
        }
        finally
        {
            await ctx.Manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task AUtpTransportThatReachedThePeerRetiresTheColdStartGuardEvenIfTheTorrentHandshakeFails()
    {
        var clock = new FakeTimeProvider();
        var ctx = CreateFallbackManager(clock, coldStartLimit: 1, markUtpEstablished: true);
        try
        {
            await ctx.Manager.StartAsync();
            ctx.Manager.ConnectTo("127.0.0.1", 6881);

            await TorrentTestUtility.WaitUntilAsync(
                () => ctx.Manager.ConnectedCount == 1,
                because: "the TCP fallback to connect");

            var anotherPeer = new PeerHistory
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6882)
            };
            Assert.Equal(["Utp", "Tcp"], Plan(ctx.Manager, ctx.Torrent.Settings.Connection, anotherPeer));
        }
        finally
        {
            await ctx.Manager.DisposeAsync();
        }
    }

    private static (PeerManager Manager, Internals.Torrent Torrent) CreatePeerManager(
        FakeTimeProvider clock, int coldStartLimit)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UtpColdStartFailureLimit = coldStartLimit;
        torrent.Settings.Connection.PreferUtp = true;
        torrent.UtpManager = new StubUtpManager();

        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            clock,
            new TorrentTestUtility.MockConnectionGovernor());

        return (manager, torrent);
    }

    private static (PeerManager Manager, Internals.Torrent Torrent, ScriptedFallbackFactory Factory)
        CreateFallbackManager(FakeTimeProvider clock, int coldStartLimit, bool markUtpEstablished)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UtpColdStartFailureLimit = coldStartLimit;
        torrent.Settings.Connection.PreferUtp = true;
        torrent.Settings.Connection.EnableUtpOut = true;
        torrent.Settings.Connection.EnableTcpOut = true;
        torrent.Settings.Connection.EnableAdaptiveTimeouts = false;
        torrent.UtpManager = new StubUtpManager();

        var factory = new ScriptedFallbackFactory(markUtpEstablished);
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            factory,
            clock,
            new TorrentTestUtility.MockConnectionGovernor());

        return (manager, torrent, factory);
    }

    private static void RecordColdStartFailure(PeerManager manager) =>
        Invoke(manager, "RecordUtpColdStartFailure");

    private static void RecordUtpSuccess(PeerManager manager) =>
        Invoke(manager, "RecordUtpSuccess");

    private static void Invoke(PeerManager manager, string name) =>
        typeof(PeerManager)
            .GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(manager, null);

    private static List<string> Plan(PeerManager manager, ConnectionSettings settings, PeerHistory history)
    {
        var method = typeof(PeerManager).GetMethod(
            "BuildTransportPlan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var plan = (System.Collections.IEnumerable)method.Invoke(manager, [settings, history, false])!;
        return [.. plan.Cast<object>().Select(p => p.ToString() ?? string.Empty)];
    }

    private sealed class StubUtpManager : Internals.Utp.IUtpManager
    {
        public Action<Internals.Utp.UtpStream>? OnNewConnection { get; set; }
        public void CloseStream(Internals.Utp.UtpStream stream) { }
        public Internals.Utp.UtpStream CreateStream(System.Net.IPEndPoint remote) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> packet, System.Net.IPEndPoint remote, CancellationToken ct) => Task.CompletedTask;
        public void Start(Internals.Network.IUdpListener listener) { }
        public void Stop() { }
    }

    private sealed class ScriptedFallbackFactory(bool markUtpEstablished) : IPeerCommunicationFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<ScriptedFallbackPeer> Created { get; } = new();

        public PeerCommunication Create(
            Internals.Torrent torrent,
            IPeerListener listener,
            TimeProvider timeProvider)
        {
            var peer = new ScriptedFallbackPeer(torrent, listener, timeProvider, markUtpEstablished);
            Created.Enqueue(peer);
            return peer;
        }

        public PeerCommunication Create(
            Internals.Torrent torrent,
            IPeerListener listener,
            TimeProvider timeProvider,
            Stream stream,
            System.Net.IPEndPoint? endpoint = null) => throw new NotSupportedException();

        public PeerCommunication Create(
            Internals.Torrent torrent,
            IPeerListener listener,
            TimeProvider timeProvider,
            System.Net.Sockets.TcpClient client) => throw new NotSupportedException();
    }

    private sealed class ScriptedFallbackPeer(
        Internals.Torrent torrent,
        IPeerListener listener,
        TimeProvider timeProvider,
        bool markUtpEstablished) : PeerCommunication(torrent, listener, timeProvider)
    {
        public List<bool> Transports { get; } = [];

        public override Task<bool> ConnectAsync(
            string ip,
            int port,
            bool useUtp,
            int timeoutMs,
            bool offerEncryption = true,
            CancellationToken ct = default)
        {
            Transports.Add(useUtp);
            IsOutgoing = true;
            RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(ip), port);

            if (useUtp)
            {
                UtpTransportEstablished = markUtpEstablished;
                return Task.FromResult(false);
            }

            Connected = 1;
            return Task.FromResult(true);
        }
    }
}
