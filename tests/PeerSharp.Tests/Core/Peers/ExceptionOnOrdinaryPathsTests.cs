using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utp;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Events that happen constantly during normal operation should not be reported by throwing.
///
/// <para>
/// A peer disconnecting is not a fault - it is most of what a BitTorrent client experiences. Reporting
/// it with an exception costs a stack capture on a per-message path, and under a debugger costs a full
/// process suspension per throw, which is enough on its own to make a consumer's application stutter.
/// Measured against a real swarm, one such failure was arriving as five separate first-chance
/// notifications as it propagated up through the stream wrappers.
/// </para>
/// </summary>
public class ExceptionOnOrdinaryPathsTests
{
    /// <summary>
    /// A closed send queue is a known state, so asking it to accept a message must not throw.
    /// </summary>
    [Fact]
    public void EnqueueingToAClosedQueueIsAnswerableWithoutThrowing()
    {
        var queue = new MessageQueue(capacity: 4);

        Assert.False(queue.IsCompleted);

        queue.TryComplete();

        Assert.True(queue.IsCompleted);

        // The distinction that matters: TryEnqueue reports false for a full queue and a closed one
        // alike, so callers need IsCompleted to avoid taking the throwing path on shutdown.
        using var msg = new PeerMessage(MessageId.KeepAlive);
        Assert.False(queue.TryEnqueue(msg));
    }

    /// <summary>
    /// A full queue must remain distinguishable from a closed one, or the short-circuit above would
    /// silently drop messages that were merely waiting for room.
    /// </summary>
    [Fact]
    public void AFullQueueIsNotReportedAsClosed()
    {
        var queue = new MessageQueue(capacity: 2);

        Assert.True(queue.TryEnqueue(new PeerMessage(MessageId.KeepAlive)));
        Assert.True(queue.TryEnqueue(new PeerMessage(MessageId.KeepAlive)));

        using var overflow = new PeerMessage(MessageId.KeepAlive);
        Assert.False(queue.TryEnqueue(overflow));
        Assert.False(queue.IsCompleted);
    }

    /// <summary>
    /// Writing to a uTP stream that is not connected reports an I/O failure.
    ///
    /// <para>
    /// The same condition checked a few lines further into the send loop has always thrown
    /// <see cref="IOException"/>. Reporting it as <see cref="InvalidOperationException"/> on entry meant
    /// one event surfaced as two different types depending on how far the write had progressed, and
    /// callers that swallow disconnects catch IOException - so the entry case escaped them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WritingToAnUnconnectedUtpStreamReportsIoFailure()
    {
        await using var manager = new UtpManager(TimeProvider.System);
        await using var stream = manager.CreateStream(new IPEndPoint(IPAddress.Loopback, 6881));

        // Never connected, so it is not in the Connected state.
        await Assert.ThrowsAsync<IOException>(async () =>
            await stream.WriteAsync(new byte[16], TestContext.Current.CancellationToken));
    }
}
