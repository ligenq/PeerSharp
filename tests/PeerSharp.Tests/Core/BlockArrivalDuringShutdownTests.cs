using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Blocks that arrive while the torrent is being disposed.
///
/// <para>
/// A peer sends data before it knows we are stopping, so blocks keep landing throughout teardown. That
/// path is driven by the peer's receive loop rather than by any loop the transfer owns, so disposal
/// does not wait for it - it cancels its token source, waits for its own background tasks, and disposes
/// the source while blocks are still coming in.
/// </para>
///
/// <para>
/// Reading <c>CancellationTokenSource.Token</c> throws once the source is disposed, so every one of
/// those late blocks faulted. A token captured while the source was alive keeps working: it reports
/// cancellation rather than throwing, and since cancellation happens before disposal, passing it to a
/// channel short-circuits instead of trying to register on a dead source.
/// </para>
/// </summary>
public class BlockArrivalDuringShutdownTests
{
    /// <summary>
    /// A late block must not throw, and must not be reported as a fault.
    ///
    /// <para>
    /// Asserting only that nothing escapes is not enough: the handler catches everything, so the
    /// original code passed that check while logging an error and capturing a stack trace for every
    /// late block. What a consumer actually saw was the first-chance exception in their debugger and an
    /// error in their log for an ordinary shutdown.
    /// </para>
    ///
    /// <para>
    /// The log is the right thing to assert on, and deliberately not AppDomain.FirstChanceException:
    /// that hook is process-wide, so it also catches exceptions raised by whatever else the suite is
    /// running in parallel, and an earlier version of this test failed intermittently for exactly that
    /// reason. The captured logger belongs to this test alone.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task BlocksArrivingAfterDisposalAreNotReportedAsFailures()
    {
        var capture = new Interop.CapturingLoggerProvider(LogLevel.Debug);
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));

        var torrent = TorrentTestUtility.CreateMinimal();
        var transfer = new FileTransfer(torrent, TimeProvider.System, loggerFactory);
        var peer = new PeerCommunication(torrent, new Peers.NullPeerListener(), TimeProvider.System);

        try
        {
            await transfer.DisposeAsync();

            // Exactly what the peer's receive loop does moments later, unaware anything has changed.
            for (int i = 0; i < 5; i++)
            {
                await transfer.BlockReceivedAsync(peer, new Block(0, i * ProtocolConstants.BlockSize, 16));
            }

            var problems = capture.SummariseProblems();
            Assert.True(
                problems.Count == 0,
                "Blocks arriving during teardown were reported as failures: " +
                string.Join("; ", problems.Select(entry => $"{entry.Message} x{entry.Count}")));
        }
        finally
        {
            await peer.DisposeAsync();
            await torrent.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    /// <summary>
    /// The same arrival interleaved with an in-progress disposal, which is the window that actually
    /// occurred rather than a strictly ordered one.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task BlocksArrivingWhileDisposalRunsAreNotReportedAsFailures()
    {
        var capture = new Interop.CapturingLoggerProvider(LogLevel.Debug);
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));

        var torrent = TorrentTestUtility.CreateMinimal();
        var transfer = new FileTransfer(torrent, TimeProvider.System, loggerFactory);
        var peer = new PeerCommunication(torrent, new Peers.NullPeerListener(), TimeProvider.System);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var sender = Task.Run(async () =>
        {
            int offset = 0;
            while (!stop.IsCancellationRequested)
            {
                await transfer.BlockReceivedAsync(peer, new Block(0, offset, 16));
                offset += ProtocolConstants.BlockSize;
            }
        });

        try
        {
            await Task.Delay(100);
            await transfer.DisposeAsync();
            await Task.Delay(300);  // Keep sending against the disposed transfer.
            await stop.CancelAsync();
            await sender;

            var problems = capture.SummariseProblems();
            Assert.True(
                problems.Count == 0,
                "Blocks arriving during teardown were reported as failures: " +
                string.Join("; ", problems.Select(entry => $"{entry.Message} x{entry.Count}")));
        }
        finally
        {
            await peer.DisposeAsync();
            await torrent.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
