using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using System.Net;
using System.Threading.Channels;
using PeerSharp.Internals.Transfers;

namespace PeerSharp.Tests.Core.Transfers;

public class PeerEvaluationSchedulerTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider;

    public PeerEvaluationSchedulerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        _timeProvider = new FakeTimeProvider();
    }

    private PeerCommunication CreatePeer()
    {
        return new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
    }

    [Fact]
    public async Task Enqueue_AddsToQueueAndDeDuplicates()
    {
        var queue = Channel.CreateUnbounded<PeerCommunication>();
        var evaluated = new List<PeerCommunication>();
        var scheduler = new PeerEvaluationScheduler(queue, p => { evaluated.Add(p); return Task.CompletedTask; }, NullLogger<PeerEvaluationScheduler>.Instance);

        var peer = CreatePeer();

        // Enqueue same peer twice, then complete the channel so RunAsync drains it and returns
        // without depending on a timing window.
        scheduler.Enqueue(peer);
        scheduler.Enqueue(peer);
        queue.Writer.Complete();

        await scheduler.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Should only be evaluated once due to de-duplication while in queue
        Assert.Single(evaluated);
        Assert.Equal(peer, evaluated[0]);
    }

    [Fact]
    public async Task Enqueue_WorksAgainAfterEvaluation()
    {
        var queue = Channel.CreateUnbounded<PeerCommunication>();
        int evaluationCount = 0;
        var firstEvaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEvaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new PeerEvaluationScheduler(queue, p =>
        {
            int count = Interlocked.Increment(ref evaluationCount);
            (count == 1 ? firstEvaluated : secondEvaluated).TrySetResult();
            return Task.CompletedTask;
        }, NullLogger<PeerEvaluationScheduler>.Instance);

        var peer = CreatePeer();

        using var cts = new CancellationTokenSource();
        var runTask = scheduler.RunAsync(cts.Token);

        // First enqueue, and wait until it has actually been evaluated (the scheduler removes the
        // peer from its de-duplication set before evaluating it).
        scheduler.Enqueue(peer);
        await firstEvaluated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Second enqueue - must be accepted because the first one is out of _queuedPeers
        scheduler.Enqueue(peer);
        await secondEvaluated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        Assert.Equal(2, Volatile.Read(ref evaluationCount));
    }

    private class MockPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, Messages.PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}