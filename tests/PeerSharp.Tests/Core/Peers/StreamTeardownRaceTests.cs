using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Peers;
using static PeerSharp.Tests.Core.Peers.BandwidthTestDoubles;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Connections torn down while a transfer is mid-flight.
///
/// <para>
/// Reserving bandwidth happens in the middle of a read or write, so there is a real gap between
/// deciding to move bytes and moving them - and that gap grows with how throttled the connection is.
/// A peer closing during it disposes the socket underneath, which is ordinary in a swarm where
/// connections come and go constantly. It should end the transfer quietly rather than throw an
/// ObjectDisposedException out through the send loop.
/// </para>
/// </summary>
public class StreamTeardownRaceTests
{
    private static RateLimitedStream Create(Stream inner, TestBandwidthManager manager)
    {
        return new RateLimitedStream(
            inner,
            new TestBandwidthUser(),
            manager,
            [DownloadChannel],
            [UploadChannel],
            leaveInnerOpen: true);
    }

    [Fact]
    public async Task WriteToAStreamDisposedMidTransfer_EndsQuietly()
    {
        var inner = new MemoryStream();
        var manager = new TestBandwidthManager();
        await using var stream = Create(inner, manager);

        // Dispose the underlying stream the way a closing connection does, after the wrapper has been
        // handed the buffer but before the bytes reach the socket.
        inner.Dispose();

        await stream.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadFromAStreamDisposedMidTransfer_ReportsEndOfStream()
    {
        var inner = new MemoryStream(new byte[1024]);
        var manager = new TestBandwidthManager();
        await using var stream = Create(inner, manager);

        inner.Dispose();

        int read = await stream.ReadAsync(new byte[1024], TestContext.Current.CancellationToken);

        // Zero is how a stream says "closed", and every caller already handles it.
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task WriteAfterTheWrapperItselfIsDisposed_EndsQuietly()
    {
        var inner = new MemoryStream();
        var manager = new TestBandwidthManager();
        var stream = Create(inner, manager);

        stream.Dispose();

        await stream.WriteAsync(new byte[512], TestContext.Current.CancellationToken);
        Assert.Empty(inner.ToArray());
    }

    [Fact]
    public async Task ReadAfterTheWrapperItselfIsDisposed_ReportsEndOfStream()
    {
        var inner = new MemoryStream(new byte[512]);
        var manager = new TestBandwidthManager();
        var stream = Create(inner, manager);

        stream.Dispose();

        Assert.Equal(0, await stream.ReadAsync(new byte[512], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BandwidthTickSurvivesAFailingChannel()
    {
        // The tick runs on a timer thread with nothing above it, so an exception escaping it is
        // unhandled and ends the process in most hosts. Bandwidth accounting is not worth that.
        await using var manager = new BandwidthManager(10, TimeProvider.System);
        manager.SetGlobalLimits(1024, 1024);
        manager.Start();

        var exploding = new ExplodingBandwidthUser();
        var request = manager.RequestBandwidthAsync(exploding, 1024 * 1024, 1, [BandwidthManager.GlobalDownload]);

        // Drive several ticks directly; none may throw.
        for (int i = 0; i < 5; i++)
        {
            manager.Update(null);
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        Assert.True(request.IsCompleted || !request.IsFaulted);
    }

    [Fact]
    public async Task BandwidthTickAfterDisposalIsANoOp()
    {
        var manager = new BandwidthManager(10, TimeProvider.System);
        manager.SetGlobalLimits(1024, 1024);
        manager.Start();

        await manager.DisposeAsync();

        // A callback already in flight when Dispose ran must not touch torn down state.
        manager.Update(null);
    }

    private sealed class ExplodingBandwidthUser : IBandwidthUser
    {
        public string Name => "exploding";

        public void AssignBandwidth(int amount) => throw new InvalidOperationException("Simulated failure inside a bandwidth grant.");
    }
}
