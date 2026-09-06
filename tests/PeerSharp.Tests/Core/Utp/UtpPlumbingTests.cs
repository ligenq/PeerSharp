using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;

namespace PeerSharp.Tests.Core.Utp;

public class UtilsTests
{
    [Fact(Timeout = 30000)]
    public void CompareSeqOrdersNumbersThatHaveNotWrapped()
    {
        Assert.Equal(-1, Utils.CompareSeq(1, 2));
        Assert.Equal(1, Utils.CompareSeq(2, 1));
        Assert.Equal(0, Utils.CompareSeq(5, 5));
    }

    [Fact(Timeout = 30000)]
    public void CompareSeqTreatsAWrapAsContinuingForward()
    {
        // Sequence numbers are 16 bit and wrap every 65,536 packets, which a long transfer reaches
        // in seconds. Comparing them as plain integers would decide that packet 0 arrived long
        // before packet 65535 and reorder the stream at every wrap.
        Assert.Equal(-1, Utils.CompareSeq(65535, 0));
        Assert.Equal(1, Utils.CompareSeq(0, 65535));
        Assert.Equal(-1, Utils.CompareSeq(65530, 5));
        Assert.Equal(1, Utils.CompareSeq(5, 65530));
    }

    [Fact(Timeout = 30000)]
    public void CompareSeqIsAntisymmetric()
    {
        foreach ((ushort a, ushort b) in new (ushort, ushort)[] { (0, 1), (100, 200), (65535, 1), (30000, 40000) })
        {
            Assert.Equal(-Utils.CompareSeq(a, b), Utils.CompareSeq(b, a));
        }
    }

    [Fact(Timeout = 30000)]
    public void HalfTheKeyspaceApartIsTheAmbiguousCaseAndReportsEqual()
    {
        // Exactly 32,768 apart is equidistant in both directions, so there is no answer to give.
        // Saying so beats picking one, because the ambiguity is real rather than a rounding detail.
        Assert.Equal(0, Utils.CompareSeq(0, 32768));
        Assert.Equal(0, Utils.CompareSeq(32768, 0));
    }

    [Fact(Timeout = 30000)]
    public void TimestampMicroAdvancesAndStaysWithinThirtyTwoBits()
    {
        // The value goes into the uTP header as a uint, and the delay measurement subtracts two of
        // them, so it has to wrap cleanly rather than saturate.
        uint first = Utils.TimestampMicro();
        for (int i = 0; i < 200000; i++) { _ = i * 2; }
        uint second = Utils.TimestampMicro();

        Assert.True(second >= first);
    }
}

public class UtpManagerTests
{
    [Fact(Timeout = 30000)]
    public void TheBigEndianHelpersRoundTripWhatTheProtocolPutsOnTheWire()
    {
        // uTP headers are big endian regardless of the host, so reading them with the machine's own
        // order would work on nothing and fail silently on everything else.
        byte[] buffer = new byte[8];

        UtpManager.WriteUInt16BigEndian(buffer, 0, 0x1234);
        Assert.Equal(0x12, buffer[0]);
        Assert.Equal(0x34, buffer[1]);
        Assert.Equal(0x1234, UtpManager.ReadUInt16BigEndian(buffer, 0));

        UtpManager.WriteUInt32BigEndian(buffer, 4, 0xDEADBEEF);
        Assert.Equal(0xDE, buffer[4]);
        Assert.Equal(0xEF, buffer[7]);
        Assert.Equal(0xDEADBEEF, UtpManager.ReadUInt32BigEndian(buffer, 4));
    }

    [Fact(Timeout = 30000)]
    public void TheBigEndianHelpersCoverTheWholeRange()
    {
        byte[] buffer = new byte[4];

        UtpManager.WriteUInt16BigEndian(buffer, 0, ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, UtpManager.ReadUInt16BigEndian(buffer, 0));

        UtpManager.WriteUInt32BigEndian(buffer, 0, uint.MaxValue);
        Assert.Equal(uint.MaxValue, UtpManager.ReadUInt32BigEndian(buffer, 0));

        UtpManager.WriteUInt32BigEndian(buffer, 0, 0);
        Assert.Equal(0u, UtpManager.ReadUInt32BigEndian(buffer, 0));
    }

    [Fact(Timeout = 30000)]
    public async Task CreateStreamHandsBackADistinctStreamPerRemote()
    {
        await using var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);

        var first = manager.CreateStream(new IPEndPoint(IPAddress.Loopback, 1000));
        var second = manager.CreateStream(new IPEndPoint(IPAddress.Loopback, 2000));

        Assert.NotSame(first, second);
        Assert.Equal(1000, first.RemoteEndPoint.Port);

        // Receive and send ids are deliberately adjacent, which is how the far side's reply is
        // matched back to this connection.
        Assert.NotEqual(first.ConnectionIdRecv, second.ConnectionIdRecv);
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingAStreamRemovesItSoTheSameRemoteCanBeDialledAgain()
    {
        await using var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);
        var remote = new IPEndPoint(IPAddress.Loopback, 1000);

        var stream = manager.CreateStream(remote);
        manager.CloseStream(stream);
        manager.CloseStream(stream); // idempotent

        Assert.NotSame(stream, manager.CreateStream(remote));
    }

    [Fact(Timeout = 30000)]
    public async Task StartRecordsTheListenerAndStopIsSafeWithoutOne()
    {
        await using var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);

        Assert.Null(manager.Listener);

        manager.Stop(); // never started

        var listener = new StubUdpListener();
        manager.Start(listener);
        Assert.Same(listener, manager.Listener);

        manager.Stop();
    }

    [Fact(Timeout = 30000)]
    public async Task ADatagramTooShortToBeAHeaderIsDiscardedRatherThanRead()
    {
        // Anything can arrive on a UDP port. A header read off the end of a four-byte datagram is
        // how a stranger's stray packet becomes an exception in the receive loop.
        await using var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);

        manager.Receive([1, 2, 3], new IPEndPoint(IPAddress.Loopback, 1));
        manager.Receive([], new IPEndPoint(IPAddress.Loopback, 1));
    }

    [Fact(Timeout = 30000)]
    public async Task APacketForAConnectionNobodyOpenedIsIgnored()
    {
        await using var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);

        byte[] packet = new byte[20];
        packet[0] = 0x01; // ST_DATA, version 1
        UtpManager.WriteUInt16BigEndian(packet, 2, 0xBEEF);

        manager.Receive(packet, new IPEndPoint(IPAddress.Loopback, 9999));
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeIsIdempotent()
    {
        var manager = new UtpManager(new FakeTimeProvider(), NullLoggerFactory.Instance);

        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    private sealed class StubUdpListener : IUdpListener
    {
        public int Port => 0;
        public void RegisterReceiver(IUdpReceiver receiver) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class UtpStreamTests
{
    [Fact(Timeout = 30000)]
    public void AFreshStreamReportsTheCapabilitiesOfAOneWayNetworkStream()
    {
        var stream = CreateStream();

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanSeek);
    }

    [Fact(Timeout = 30000)]
    public void TheSeekableStreamOperationsSaySoRatherThanMisbehaving()
    {
        // A connection has no length and no position. Answering zero would let a caller that seeks
        // believe it succeeded and read from the wrong place.
        var stream = CreateStream();

        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact(Timeout = 30000)]
    public void FlushIsANoOpBecauseSendingIsWhatWritingDoes()
    {
        var stream = CreateStream();

        stream.Flush();
    }

    [Fact(Timeout = 30000)]
    public void AFreshStreamCarriesTheIdentityItWasCreatedWith()
    {
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 6881);
        var stream = new UtpStream(new StubUtpManager(), remote, idRecv: 100, idSend: 101, timeProvider: new FakeTimeProvider());

        Assert.Same(remote, stream.RemoteEndPoint);
        Assert.Equal(100, stream.ConnectionIdRecv);
        Assert.Equal(101, stream.ConnectionIdSend);
        Assert.Equal(0, stream.SentPacketsCount);
    }

    [Fact(Timeout = 30000)]
    public void CheckTimeoutOnAnIdleStreamDoesNotThrow()
    {
        // The manager ticks every stream on a timer, including ones that have never connected.
        var stream = CreateStream();

        stream.CheckTimeout();
        stream.CheckTimeout();
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectGivesUpRatherThanHangingWhenNothingAnswers()
    {
        // The stub manager drops every packet, which is what a NAT that never replies looks like -
        // the majority outcome for addresses a swarm hands out.
        var clock = new FakeTimeProvider();
        var stream = new UtpStream(new StubUtpManager(), new IPEndPoint(IPAddress.Loopback, 1), 1, 2, clock);

        var connecting = stream.ConnectAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        // Stepped rather than advanced once: the deadline timer is created after an await inside
        // ConnectAsync, so a single advance can land before there is anything to fire.
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => connecting.IsCompleted, TimeSpan.FromMilliseconds(50));

        Assert.False(await connecting);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectIsCancellable()
    {
        var stream = CreateStream();
        using var cts = new CancellationTokenSource();

        var connecting = stream.ConnectAsync(TimeSpan.FromMinutes(5), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connecting);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectRefusesToRunTwiceOnTheSameStream()
    {
        // A second dial on a connected stream would reset the sequence numbers underneath the first,
        // so it is refused rather than quietly restarted.
        var clock = new FakeTimeProvider();
        var stream = new UtpStream(new StubUtpManager(), new IPEndPoint(IPAddress.Loopback, 1), 1, 2, clock);

        var connecting = stream.ConnectAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ConnectAsync(TimeSpan.FromMilliseconds(50)));

        await TorrentTestUtility.AdvanceUntilAsync(clock, () => connecting.IsCompleted, TimeSpan.FromMilliseconds(50));
        await connecting;
    }

    [Fact(Timeout = 30000)]
    public void CloseIsSafeAndRepeatable()
    {
        var stream = CreateStream();

        stream.Close();
        stream.Close();
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeAsyncIsSafeAndRepeatable()
    {
        var stream = CreateStream();

        await stream.DisposeAsync();
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task AReadWithNothingToReadWaitsAndIsCancellable()
    {
        // A uTP stream with no buffered data waits for the far side, exactly as a socket does. The
        // caller's token is the way out, and it is the only one - which is why a read on a peer
        // that has gone quiet must always be given one.
        var stream = CreateStream();
        using var cts = new CancellationTokenSource();

        var reading = stream.ReadAsync(new byte[16], cts.Token);
        Assert.False(reading.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
    }

    [Fact(Timeout = 30000)]
    public void SynchronousReadAndWriteAreRefusedRatherThanBlockingTheCallersThread()
    {
        // Blocking on a uTP read means blocking on the network from whatever thread the caller is
        // on. Saying so is better than a deadlock whose cause the caller cannot see.
        var stream = CreateStream();

        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[8], 0, 8));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[8], 0, 8));
    }

    [Fact(Timeout = 30000)]
    public async Task TheByteArrayReadOverloadIsCancellableTheSameWay()
    {
        var stream = CreateStream();
        using var cts = new CancellationTokenSource();

        var reading = stream.ReadAsync(new byte[8], 0, 8, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
    }

    private static UtpStream CreateStream() =>
        new(new StubUtpManager(), new IPEndPoint(IPAddress.Loopback, 12345), idRecv: 1, idSend: 2, timeProvider: new FakeTimeProvider());

    /// <summary>A manager that accepts every send and delivers nothing, like an unanswered dial.</summary>
    private sealed class StubUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void CloseStream(UtpStream stream) { }
        public UtpStream CreateStream(IPEndPoint remote) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct) => Task.CompletedTask;
        public void Start(IUdpListener listener) { }
        public void Stop() { }
    }
}
