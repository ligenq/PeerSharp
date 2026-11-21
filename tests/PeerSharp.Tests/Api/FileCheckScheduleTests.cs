using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Api;

public sealed class FileCheckScheduleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [InlineData("Stop, Pause, Read")]
    [InlineData("Stop, Read, Pause")]
    [InlineData("Pause, Stop, Read")]
    [InlineData("Pause, Read, Stop")]
    [InlineData("Read, Stop, Pause")]
    [InlineData("Read, Pause, Stop")]
    public async Task AnExplicitStopWinsAcrossPauseAndFileCheckCompletion(string schedule)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("PeerSharpFileCheckSchedule-");
        try
        {
            await RunAsync(root.FullName, schedule, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunAsync(string path, string schedule, CancellationToken ct)
    {
        const string name = "existing.bin";
        byte[] payload = Enumerable.Repeat((byte)0x63, ProtocolConstants.BlockSize).ToArray();
        var file = TorrentLifecycleModelTests.MakeTorrent(name, payload);
        await File.WriteAllBytesAsync(Path.Combine(path, name), payload, ct);
        using var handles = new PausedRead();
        await using (var engine = TorrentLifecycleModelTests.CreateEngine(path, new FakeTimeProvider(), queue: false, fileHandleCache: handles))
        {
            await engine.InitializeAsync(ct);
            var torrent = (Torrent)await engine.AddTorrentAsync(file, new AddTorrentOptions(path) { StartImmediately = false }, ct);
            Task start = torrent.StartAsync(ct);
            var pending = new List<Task> { start };
            int expectedPieces = 0;
            bool readReleased = false;
            try
            {
                await handles.Entered.Task.WaitAsync(Timeout, ct);
                Assert.Equal(TorrentState.CheckingFiles, torrent.State);
                Assert.False(torrent.Started);

                foreach (string step in schedule.Split(", "))
                {
                    switch (step)
                    {
                        case "Read":
                            expectedPieces = pending.Count == 1 ? 1 : 0;
                            handles.Release.SetResult();
                            readReleased = true;
                            await Task.WhenAll(pending).WaitAsync(Timeout, ct);
                            Assert.Equal(expectedPieces, torrent.PiecesReceived);
                            break;
                        case "Stop":
                            torrent.QueueAutoStart = false;
                            pending.Add(torrent.StopAsync(ct));
                            break;
                        case "Pause":
                            pending.Add(engine.PauseAsync(ct));
                            break;
                    }
                    if (readReleased)
                    {
                        await Task.WhenAll(pending).WaitAsync(Timeout, ct);
                    }
                    else
                    {
                        long requestedStops = pending.Count - 1;
                        // A stop counts itself before it cancels the running check, so wait for the
                        // cancellation to reach the read too. Otherwise the read can be released in
                        // between and finish the check before the stop gets to it.
                        await TorrentTestUtility.WaitUntilAsync(
                            () => torrent.StopRequests >= requestedStops && handles.Cancellation.IsCancellationRequested,
                            because: $"{step} to reach the torrent before the read is released");
                        Assert.False(start.IsCompleted);
                    }
                }
                Assert.Equal(expectedPieces == 0, handles.Cancellation.IsCancellationRequested);
                Assert.True(engine.IsPaused);
                Assert.False(torrent.Started);
                Assert.False(engine.IsHolding(torrent));
            }
            finally
            {
                handles.Release.TrySetResult();
                await Task.WhenAll(pending).WaitAsync(Timeout);
            }
            await engine.SaveSessionAsync(ct);
        }

        await using var restored = TorrentLifecycleModelTests.CreateEngine(path, new FakeTimeProvider(), queue: false);
        await restored.InitializeAsync(ct);
        var back = Assert.Single(restored.GetTorrents());
        Assert.True(restored.IsPaused);
        await restored.ResumeAsync(ct);
        Assert.Equal(TorrentState.Stopped, back.State);
        Assert.False(back.QueueAutoStart);
        await back.StartAsync(ct);
        Assert.True(back.Finished);
    }

    private sealed class PausedRead : IFileHandleCache
    {
        private readonly FileHandleCache _inner = new();
        private int _used;

        public CancellationToken Cancellation { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
        {
            if (!writable && Interlocked.Exchange(ref _used, 1) == 0)
            {
                Cancellation = cancellationToken;
                Entered.SetResult();
                // Model I/O that observes cancellation only once the device answers.
                await Release.Task;
            }
            return await _inner.GetHandleAsync(path, writable, cancellationToken);
        }

        public void CloseTorrentHandles(string rootPath) => _inner.CloseTorrentHandles(rootPath);
        public void Dispose() => _inner.Dispose();
    }
}
