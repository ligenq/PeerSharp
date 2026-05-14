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

        // Enqueue same peer twice
        scheduler.Enqueue(peer);
        scheduler.Enqueue(peer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var runTask = scheduler.RunAsync(cts.Token);

        // Wait a bit for processing
        await Task.Delay(100);
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        // Should only be evaluated once due to de-duplication while in queue
        Assert.Single(evaluated);
        Assert.Equal(peer, evaluated[0]);
    }

    [Fact]
    public async Task Enqueue_WorksAgainAfterEvaluation()
    {
        var queue = Channel.CreateUnbounded<PeerCommunication>();
        int evaluationCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new PeerEvaluationScheduler(queue, p =>
        {
            evaluationCount++;
            if (evaluationCount == 2)
            {
                tcs.TrySetResult();
            }

            return Task.CompletedTask;
        }, NullLogger<PeerEvaluationScheduler>.Instance);

        var peer = CreatePeer();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var runTask = scheduler.RunAsync(cts.Token);

        // First enqueue
        scheduler.Enqueue(peer);

        // Give time to process
        await Task.Delay(50);

        // Second enqueue - should work now because first one is out of _queuedPeers
        scheduler.Enqueue(peer);

        await Task.WhenAny(tcs.Task, Task.Delay(500));
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        Assert.Equal(2, evaluationCount);
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