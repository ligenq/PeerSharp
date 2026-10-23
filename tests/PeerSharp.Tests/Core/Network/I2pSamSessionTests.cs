using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Network;

public sealed class I2pSamSessionTests
{
    [Theory]
    [InlineData("abc\nSTREAM CONNECT ID=bad")]
    [InlineData("name with spaces.i2p")]
    [InlineData("")]
    public void SamCommandsRejectInjectedDestinationTokens(string destination)
        => Assert.Throws<ArgumentException>(() => I2pSamSession.ValidateDestination(destination));

    [Fact]
    public void I2pTrackerPeersUse32ByteHashesAndNeverBecomeIpEndpoints()
    {
        var dict = new BDict();
        dict.Dict["peers"] = new BString(new byte[64]);
        var response = HttpTracker.ParseResponse(BencodeWriter.Write(dict), i2p: true);
        Assert.Empty(response.Peers);
        Assert.Equal(2, response.I2pPeers.Count);
        Assert.Equal(new string('a', 52) + ".b32.i2p", response.I2pPeers[0]);
        dict.Dict["peers"] = new BString(new byte[6]);
        var truncated = HttpTracker.ParseResponse(BencodeWriter.Write(dict), i2p: true);
        Assert.Empty(truncated.I2pPeers);
        Assert.Empty(truncated.Peers);
    }

    [Fact(Timeout = 15000)]
    public async Task ConcurrentConnectionsShareOneSamSessionAndPreserveCoalescedPayload()
    {
        await using var bridge = new SamBridge();
        var incoming = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            async (stream, ct) =>
            {
                await using (stream) { byte[] data = new byte[3]; await stream.ReadExactlyAsync(data, ct); incoming.TrySetResult(data); }
            });
        var connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => session.ConnectAsync("peer.b32.i2p", TestContext.Current.CancellationToken)));
        foreach (var stream in connections)
        {
            await using (stream) { byte[] data = new byte[3]; await stream.ReadExactlyAsync(data, TestContext.Current.CancellationToken); Assert.Equal(new byte[] { 19, 1, 2 }, data); }
        }
        Assert.Equal(new byte[] { 19, 3, 4 }, await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, bridge.Sessions);
        Assert.NotNull(session.Destination);
        Assert.True(I2pSamSession.IsI2pUrl("http://tracker.i2p/announce"));
        Assert.False(I2pSamSession.IsI2pUrl("http://tracker.i2p.example/announce"));
    }

    /// <summary>
    /// Inbound streams whose handshake stalls must not consume the inbound path. More of them are
    /// stalled here than the session keeps STREAM ACCEPTs outstanding, so this only passes if the
    /// accept loop hands each stream off instead of awaiting it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StalledInboundHandshakesDoNotBlockFurtherInboundStreams()
    {
        const int stalled = 8;
        await using var bridge = new SamBridge { AcceptsServed = stalled + 1 };
        using var release = new CancellationTokenSource();
        int entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            async (stream, ct) =>
            {
                await using (stream)
                {
                    byte[] data = new byte[3];
                    await stream.ReadExactlyAsync(data, ct);
                    Assert.Equal(new byte[] { 19, 3, 4 }, data);
                    if (Interlocked.Increment(ref entered) >= stalled + 1) allEntered.TrySetResult();
                    // Hold every handler open, so passing cannot depend on one finishing early.
                    try { await Task.Delay(Timeout.Infinite, release.Token); }
                    catch (OperationCanceledException) { }
                }
            });

        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(stalled + 1, Volatile.Read(ref entered));
        await release.CancelAsync();
    }

    /// <summary>
    /// A control socket that closes ends that session generation: the monitor drops the destination,
    /// stops the accept loops and disposes the token source they share. Disposing the session
    /// afterwards must stay quiet, which needs the source to outlive every loop that observes it and
    /// the monitor to treat a closed or disposed socket as the end of the session rather than as a
    /// fault to propagate out of <c>DisposeAsync</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ClosedControlSocketFollowedByDisposalTearsTheSessionDownQuietly()
    {
        await using var bridge = new SamBridge { CloseControlAfterCreate = true };
        var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            (stream, _) => stream.DisposeAsync().AsTask());
        await using (session)
        {
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
            while (session.Destination != null) await Task.Delay(10, TestContext.Current.CancellationToken);
            // Explicit, so a faulted background task surfaces here rather than from the scope exit.
            await session.DisposeAsync();
        }
        Assert.Null(session.Destination);
    }

    /// <summary>
    /// <see cref="Torrent.IsI2p"/> is read per handshake and per connection, so the tracker scan it
    /// used to do on every read is kept. The kept answer still has to follow the torrent: an
    /// announce list that gains an I2P tracker, or a replaced one that loses it, must be noticed,
    /// and a torrent told it is I2P stays I2P whatever its trackers say.
    /// </summary>
    [Fact]
    public async Task I2pDetectionIsCachedButStillFollowsTheAnnounceList()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        Assert.False(torrent.IsI2p);
        object? first = Scan(torrent);
        Assert.False(torrent.IsI2p);
        Assert.Same(first, Scan(torrent)); // a second read reuses the scan instead of repeating it

        torrent.InfoFile.AnnounceList.Add("http://tracker.i2p/announce");
        Assert.True(torrent.IsI2p);
        Assert.NotSame(first, Scan(torrent));

        torrent.InfoFile.AnnounceList = ["http://tracker.example/announce"];
        Assert.False(torrent.IsI2p);

        torrent.InfoFile.Announce = "http://announce.i2p/a";
        Assert.True(torrent.IsI2p);

        torrent.InfoFile.Announce = "http://announce.example/a";
        Assert.False(torrent.IsI2p);

        torrent.IsI2p = true;
        Assert.True(torrent.IsI2p);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task I2pDetectionFollowsTrackerEditsThatKeepTheListAndItsCount(bool replaceByRemoveAndAdd)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        var trackers = torrent.InfoFile.AnnounceList;
        trackers.Add("http://public.example/announce");
        Assert.False(torrent.IsI2p);

        Replace("http://tracker.i2p/announce");
        Assert.True(torrent.IsI2p);
        Replace("http://public.example/announce");
        Assert.False(torrent.IsI2p);

        void Replace(string url)
        {
            if (replaceByRemoveAndAdd) { trackers.RemoveAt(0); trackers.Add(url); }
            else trackers[0] = url;
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData("ALREADY_ACCEPTING", 30, 3)]
    [InlineData("I2P_ERROR", 5, 4)]
    public async Task RouterRejectionsBackOffWithoutBusyRetrying(string result, int seconds, int retryingAcceptors)
    {
        await using var bridge = new SamBridge { AcceptsServed = 0, AcceptFailureResult = result };
        var time = new ObservedTimeProvider();
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, time, NullLogger.Instance,
            (stream, _) => stream.DisposeAsync().AsTask());
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        for (int i = 0; i < retryingAcceptors; i++)
        {
            var delay = await time.Delays.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
        }
        Assert.Equal(4, bridge.AcceptCommands);

        time.Clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Equal(4, bridge.AcceptCommands);
        time.Clock.Advance(TimeSpan.FromSeconds(1));
        await TorrentTestUtility.WaitUntilAsync(() => bridge.AcceptCommands == 4 + retryingAcceptors);
        Assert.Equal(1, bridge.Sessions);
    }

    [Fact(Timeout = 15000)]
    public async Task AClosedControlSessionCanBeRecreatedAndUsedForNewConnections()
    {
        await using var bridge = new SamBridge { AcceptsServed = 0 };
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            (stream, _) => stream.DisposeAsync().AsTask());
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        string? firstDestination = session.Destination;
        Assert.NotNull(firstDestination);
        bridge.CloseControl(1);
        await TorrentTestUtility.WaitUntilAsync(() => session.Destination == null);

        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(session.Destination);
        Assert.NotEqual(firstDestination, session.Destination);
        await using var connected = await session.ConnectAsync(new string('D', 516), TestContext.Current.CancellationToken);
        byte[] payload = new byte[3];
        await connected.ReadExactlyAsync(payload, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 19, 1, 2 }, payload);
        Assert.Equal(2, bridge.Sessions);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailingInboundHandlerClosesItsStreamAndDoesNotStopAccepting(bool throwsSynchronously)
    {
        await using var bridge = new SamBridge { AcceptsServed = 2 };
        var failedStream = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            (stream, ct) =>
            {
                if (Interlocked.Increment(ref entered) == 1)
                {
                    failedStream.TrySetResult(stream);
                    if (throwsSynchronously) throw new InvalidOperationException("synchronous handler failure");
                    return FailLaterAsync(ct);
                }
                nextHandled.TrySetResult();
                return stream.DisposeAsync().AsTask();
            });
        try
        {
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
            Stream rejected = await failedStream.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            releaseFailure.TrySetResult();
            await TorrentTestUtility.WaitUntilAsync(() => !rejected.CanRead);
            Assert.Equal(2, Volatile.Read(ref entered));
        }
        finally { releaseFailure.TrySetResult(); }

        async Task FailLaterAsync(CancellationToken ct)
        {
            await releaseFailure.Task.WaitAsync(ct);
            throw new InvalidOperationException("asynchronous handler failure");
        }
    }

    [Fact(Timeout = 15000)]
    public async Task DisposingWhileTheBridgeHasNotAnsweredHelloCancelsInitialization()
    {
        await using var bridge = new SamBridge { StallHelloResponse = true };
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            (stream, _) => stream.DisposeAsync().AsTask());
        Task initialize = session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        await bridge.HelloReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        Assert.Null(session.Destination);
        Assert.Equal(0, bridge.Sessions);
    }

    [Fact(Timeout = 15000)]
    public async Task DisposingTheSessionCancelsOutstandingInboundHandshakes()
    {
        await using var bridge = new SamBridge();
        var entered = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new I2pSamSession("127.0.0.1", bridge.Port, TimeProvider.System, NullLogger.Instance,
            async (stream, ct) =>
            {
                try
                {
                    await using (stream)
                    {
                        entered.TrySetResult(stream);
                        try { await Task.Delay(Timeout.Infinite, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Session shutdown. */ }
                    }
                }
                finally { cancelled.TrySetResult(); }
            });
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        Stream incoming = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(incoming.CanRead);
    }

    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        public FakeTimeProvider Clock { get; } = new();
        public ChannelReader<TimeSpan> Delays => _delays.Reader;
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override long GetTimestamp() => Clock.GetTimestamp();
        public override long TimestampFrequency => Clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = Clock.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private static object? Scan(Torrent torrent) => typeof(Torrent)
        .GetField("_i2pScan", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(torrent);

    private sealed class SamBridge : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _loop;
        private int _sessions;
        private int _accepts;
        private readonly ConcurrentDictionary<int, TcpClient> _controls = new();
        /// <summary>How many STREAM ACCEPTs receive an inbound stream; the rest stay pending.</summary>
        public int AcceptsServed { get; init; } = 1;
        public bool StallHelloResponse { get; init; }
        public TaskCompletionSource HelloReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Whether the control socket closes once the session has been created.</summary>
        public bool CloseControlAfterCreate { get; init; }
        /// <summary>Simulate routers with a single accept slot, or a general accept failure.</summary>
        public string? AcceptFailureResult { get; init; }
        public int AcceptCommands => Volatile.Read(ref _accepts);
        public void CloseControl(int generation) => _controls[generation].Dispose();
        public int Sessions => Volatile.Read(ref _sessions);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public SamBridge() { _listener.Start(); _loop = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    Assert.Equal("HELLO VERSION MIN=3.1 MAX=3.1", await I2pSamSession.ReadLineAsync(stream, _stop.Token));
                    HelloReceived.TrySetResult();
                    if (StallHelloResponse) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                    await stream.WriteAsync("HELLO REPLY RESULT=OK VERSION=3.1\n"u8.ToArray(), _stop.Token);
                    string command = await I2pSamSession.ReadLineAsync(stream, _stop.Token);
                    if (command.StartsWith("SESSION CREATE", StringComparison.Ordinal))
                    {
                        int generation = Interlocked.Increment(ref _sessions);
                        _controls[generation] = client;
                        Assert.Contains("SIGNATURE_TYPE=7", command);
                        await stream.WriteAsync("SESSION STATUS RESULT=OK DESTINATION=private\n"u8.ToArray(), _stop.Token);
                        Assert.Equal("NAMING LOOKUP NAME=ME", await I2pSamSession.ReadLineAsync(stream, _stop.Token));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("NAMING REPLY RESULT=OK VALUE=" + new string((char)('A' + generation - 1), 516) + "\n"), _stop.Token);
                        // Closing here is how a router ending the session looks to the monitor.
                        if (!CloseControlAfterCreate) await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    else if (command.StartsWith("NAMING LOOKUP", StringComparison.Ordinal))
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("NAMING REPLY RESULT=OK VALUE=" + new string('B', 516) + "\n"), _stop.Token);
                    else if (command.StartsWith("STREAM CONNECT", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync("STREAM STATUS RESULT=OK\n"u8.ToArray(), _stop.Token);
                        await stream.WriteAsync(new byte[] { 19, 1, 2 }, _stop.Token);
                    }
                    else if (command.StartsWith("STREAM ACCEPT", StringComparison.Ordinal))
                    {
                        int ordinal = Interlocked.Increment(ref _accepts);
                        if (AcceptFailureResult != null && (AcceptFailureResult != "ALREADY_ACCEPTING" || ordinal != 1))
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"STREAM STATUS RESULT={AcceptFailureResult}\n"), _stop.Token);
                            return;
                        }
                        await stream.WriteAsync("STREAM STATUS RESULT=OK\n"u8.ToArray(), _stop.Token);
                        if (ordinal <= AcceptsServed)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('C', 516) + "\n").Concat(new byte[] { 19, 3, 4 }).ToArray(), _stop.Token);
                        }
                        else await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (EndOfStreamException) { }
                // A session that stops mid-command resets its sockets. A real router shrugs that off
                // per connection, and so does this one, rather than failing the whole bridge.
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop();
            await _loop; await Task.WhenAll(_connections); _stop.Dispose();
        }
    }
}
