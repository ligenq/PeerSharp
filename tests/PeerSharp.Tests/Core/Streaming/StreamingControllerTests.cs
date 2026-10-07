using PeerSharp.Internals;
using PeerSharp.Streaming;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Streaming;

public class StreamingControllerTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly StreamingController _controller;

    public StreamingControllerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        // Setup files including streamable and non-streamable
        _torrent.InfoFile.Info.PieceSize = 1000;
        _torrent.InfoFile.Info.FullSize = 20000;
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "video.mp4", Size = 10000, Offset = 0 });
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "readme.txt", Size = 5000, Offset = 10000 });
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "audio.mp3", Size = 5000, Offset = 15000 });
        _torrent.InfoFile.Info.Pieces.Clear();
        for (int i = 0; i < 20; i++)
        {
            _torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        }

        _torrent.ReinitializeAfterMetadataAsync().GetAwaiter().GetResult();
        _controller = _torrent.Streaming;
    }

    #region Properties Tests

    [Fact]
    public void NothingIsStreamingUntilAStreamIsOpened()
    {
        Assert.False(_controller.IsStreaming);
        Assert.Null(_controller.PriorityPieces);
        Assert.Equal(DownloadStrategy.RarestFirst, _torrent.EffectiveDownloadStrategy);
    }

    #endregion

    #region Streamable Files Tests

    [Fact]
    public void HasStreamableFiles_ReturnsTrue_WhenStreamableFilesExist()
    {
        Assert.True(_controller.HasStreamableFiles);
    }

    [Fact]
    public async Task HasStreamableFiles_ReturnsFalse_WhenNoStreamableFiles()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "readme.txt", Size = 1000, Offset = 0 });
        torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        await torrent.ReinitializeAfterMetadataAsync();

        Assert.False(torrent.Streaming.HasStreamableFiles);
    }

    [Fact]
    public void StreamableFileIndices_ReturnsCorrectIndices()
    {
        var indices = _controller.StreamableFileIndices;

        Assert.Equal(2, indices.Count);
        Assert.Contains(0, indices); // video.mp4
        Assert.Contains(2, indices); // audio.mp3
        Assert.DoesNotContain(1, indices); // readme.txt
    }

    [Fact]
    public async Task StreamableFileIndices_RecognizesAllMediaExtensions()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var extensions = new[] { ".mp4", ".mkv", ".avi", ".mp3", ".flac", ".webm", ".mov" };
        int offset = 0;
        foreach (var ext in extensions)
        {
            torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = $"file{ext}", Size = 1000, Offset = offset });
            torrent.InfoFile.Info.Pieces.Add(new byte[20]);
            offset += 1000;
        }
        torrent.InfoFile.Info.FullSize = offset;
        await torrent.ReinitializeAfterMetadataAsync();

        Assert.Equal(extensions.Length, torrent.Streaming.StreamableFileIndices.Count);
    }

    #endregion

    #region OpenStreamAsync Tests

    [Fact(Timeout = 30000)]
    public async Task OpenStreamAsync_ReturnsStream()
    {
        await _torrent.StartAsync();

        var stream = await _controller.OpenStreamAsync(0);

        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);

        stream.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task OpenStreamAsync_PutsThePickerIntoStreamingMode()
    {
        await _torrent.StartAsync();

        await using var stream = await _controller.OpenStreamAsync(0);

        Assert.True(_controller.IsStreaming);
        Assert.Equal(DownloadStrategy.Streaming, _torrent.EffectiveDownloadStrategy);
        Assert.NotNull(_controller.PriorityPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task OpeningAStream_DoesNotChangeTheConfiguredStrategy()
    {
        // The configured strategy is what gets saved with the session. Streaming is an override for
        // as long as a stream is open, and must not be persisted as though it were the user's choice.
        _torrent.DownloadStrategy = DownloadStrategy.Sequential;
        await _torrent.StartAsync();

        await using var stream = await _controller.OpenStreamAsync(0);

        Assert.Equal(DownloadStrategy.Sequential, _torrent.DownloadStrategy);
        Assert.Equal(DownloadStrategy.Streaming, _torrent.EffectiveDownloadStrategy);
    }

    [Fact(Timeout = 30000)]
    public async Task OpenStreamAsync_ThrowsWhenTorrentStopped()
    {
        // Torrent is stopped by default
        Assert.Equal(TorrentState.Stopped, _torrent.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _controller.OpenStreamAsync(0));
    }

    [Fact(Timeout = 30000)]
    public async Task OpenStreamAsync_ThrowsForInvalidFileIndex()
    {
        await _torrent.StartAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _controller.OpenStreamAsync(-1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _controller.OpenStreamAsync(99));
    }

    [Fact(Timeout = 30000)]
    public async Task EveryOpenStream_HasItsPiecesPrioritised()
    {
        // Two streams at once is ordinary: a player reading the playhead while fetching an index from
        // the end of the file, or two files played side by side. Neither may lose its priorities.
        await _torrent.StartAsync();

        await using var video = await _controller.OpenStreamAsync(0);
        await using var audio = await _controller.OpenStreamAsync(2);

        Assert.Contains(0, _controller.PriorityPieces!);  // video.mp4 starts at piece 0
        Assert.Contains(15, _controller.PriorityPieces!); // audio.mp3 starts at piece 15
    }

    [Fact(Timeout = 30000)]
    public async Task TheStreamReadMostRecently_HasItsPiecesFirst()
    {
        await _torrent.StartAsync();

        await using var video = await _controller.OpenStreamAsync(0);
        await using var audio = await _controller.OpenStreamAsync(2);
        Assert.Equal(15, _controller.PriorityPieces![0]);

        video.Seek(5000, SeekOrigin.Begin);

        Assert.Equal(5, _controller.PriorityPieces![0]);
    }

    #endregion

    #region OnPieceVerified Tests

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_WakesAnOlderStreamToo()
    {
        await _torrent.StartAsync();
        await using var older = await _controller.OpenStreamAsync(0);
        await using var newer = await _controller.OpenStreamAsync(2);

        var buffer = new byte[100];
        var readTask = older.ReadAsync(buffer, 0, 100);
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        _torrent.Pieces.AddPiece(0);
        _controller.OnPieceVerified(0);

        // Well inside the one-second poll the reader would otherwise fall back on.
        int read = await readTask.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(100, read);
    }

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_ForwardsToActiveStream()
    {
        await _torrent.StartAsync();
        await using var stream = await _controller.OpenStreamAsync(0);

        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100);

        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        // Add piece and notify through controller
        _torrent.Pieces.AddPiece(0);
        _controller.OnPieceVerified(0);

        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(100, read);
    }

    [Fact]
    public void OnPieceVerified_HandlesNullStream()
    {
        // No exception should be thrown when no stream is active
        _controller.OnPieceVerified(0);
    }

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_HandlesDisposedStream()
    {
        await _torrent.StartAsync();
        var stream = await _controller.OpenStreamAsync(0);
        stream.Dispose();

        // Should not throw after stream is disposed
        _controller.OnPieceVerified(0);
    }

    #endregion

    #region OnStreamDisposed Tests

    [Fact(Timeout = 30000)]
    public async Task ClosingTheLastStream_EndsStreamingMode()
    {
        await _torrent.StartAsync();
        var stream = await _controller.OpenStreamAsync(0);

        Assert.NotNull(_controller.PriorityPieces);

        stream.Dispose();

        Assert.False(_controller.IsStreaming);
        Assert.Null(_controller.PriorityPieces);
        Assert.Equal(DownloadStrategy.RarestFirst, _torrent.EffectiveDownloadStrategy);
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingTheNewerStream_KeepsTheOlderOneStreaming()
    {
        // The regression this design exists for. A Chromecast reads an MP4 from the playhead on one
        // request and fetches its index from the end on another, then closes that one. Only the
        // latest stream used to be tracked, so closing it put the whole torrent back on rarest-first
        // while the playhead's reader was still open - and that reader stalled until it timed out.
        await _torrent.StartAsync();

        await using var playhead = await _controller.OpenStreamAsync(0);
        var index = await _controller.OpenStreamAsync(0);
        index.Seek(-500, SeekOrigin.End);

        await index.DisposeAsync();

        Assert.True(_controller.IsStreaming);
        Assert.Equal(DownloadStrategy.Streaming, _torrent.EffectiveDownloadStrategy);
        Assert.Contains(0, _controller.PriorityPieces!);
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingTheOlderStreamFirst_AlsoKeepsStreaming()
    {
        await _torrent.StartAsync();

        var older = await _controller.OpenStreamAsync(0);
        await using var newer = await _controller.OpenStreamAsync(2);

        older.Dispose();

        Assert.True(_controller.IsStreaming);
        Assert.DoesNotContain(0, _controller.PriorityPieces!);
        Assert.Contains(15, _controller.PriorityPieces!);
    }

    [Fact(Timeout = 30000)]
    public async Task AfterStreaming_TheConfiguredStrategyAppliesAgain()
    {
        // Used to be reset to rarest-first regardless, so a torrent added as sequential silently
        // stopped being sequential the first time anyone streamed from it.
        _torrent.DownloadStrategy = DownloadStrategy.Sequential;
        await _torrent.StartAsync();

        var stream = await _controller.OpenStreamAsync(0);
        stream.Dispose();

        Assert.Equal(DownloadStrategy.Sequential, _torrent.EffectiveDownloadStrategy);
        Assert.Equal(DownloadStrategy.Sequential, _torrent.DownloadStrategy);
    }

    [Fact]
    public async Task TheConfiguredStrategy_SurvivesTheMetadataArriving()
    {
        // Initialize runs again when a magnet's metadata arrives. It used to build a new controller,
        // which started from rarest-first whatever the torrent had been configured with.
        _torrent.DownloadStrategy = DownloadStrategy.Sequential;

        await _torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStrategy.Sequential, _torrent.EffectiveDownloadStrategy);
    }

    [Fact]
    public async Task TheController_SurvivesTheMetadataArriving()
    {
        // A stream opened on a magnet link waits for the metadata while registered with this
        // controller. Replacing the controller when the metadata arrived left that stream's
        // priorities somewhere the picker no longer looked.
        var before = _torrent.Streaming;

        await _torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Same(before, _torrent.Streaming);
    }

    [Fact]
    public async Task DisposingAStreamTwice_OnlyForgetsItOnce()
    {
        await _torrent.StartAsync(TestContext.Current.CancellationToken);
        var stream = Assert.IsType<TorrentStream>(await _controller.OpenStreamAsync(0, TestContext.Current.CancellationToken));

        Assert.True(_controller.OnStreamDisposed(stream));
        Assert.False(_controller.OnStreamDisposed(stream));

        stream.Dispose();
    }

    [Fact]
    public async Task AClosedStream_IsNotTrackedAgainByALateRead()
    {
        // A read racing its own stream's disposal updates priorities after the controller has let
        // the stream go. Tracking it again would keep the torrent streaming for nobody.
        await _torrent.StartAsync(TestContext.Current.CancellationToken);
        var stream = Assert.IsType<TorrentStream>(await _controller.OpenStreamAsync(0, TestContext.Current.CancellationToken));
        stream.Dispose();

        _controller.UpdatePriorities(stream, [1, 2, 3]);

        Assert.False(_controller.IsStreaming);
        Assert.Null(_controller.PriorityPieces);
    }

    #endregion

    #region Metadata Wait Tests

    [Fact]
    public async Task OpeningAStreamOnAMagnet_TimesOutWhenTheMetadataNeverArrives()
    {
        // A timeout, not a cancellation: nobody asked for this to stop, so a caller must be able to
        // tell "the swarm never answered" from "I gave up".
        var time = new FakeTimeProvider();
        var magnet = TorrentTestUtility.CreateMinimal(timeProvider: time);
        magnet.Settings.Streaming.MetadataWaitTimeoutSeconds = 5;
        await magnet.StartAsync(TestContext.Current.CancellationToken);

        var opening = magnet.Streaming.OpenStreamAsync(0, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => opening);
    }

    [Fact]
    public async Task OpeningAStreamOnAMagnet_CanBeCancelledByTheCaller()
    {
        var magnet = TorrentTestUtility.CreateMinimal(timeProvider: new FakeTimeProvider());
        await magnet.StartAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var opening = magnet.Streaming.OpenStreamAsync(0, cancel.Token);
        await cancel.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.IsNotType<TimeoutException>(thrown);
    }

    [Fact]
    public async Task AMetadataTimeoutOfZero_WaitsForAsLongAsTheCallerAllows()
    {
        var time = new FakeTimeProvider();
        var magnet = TorrentTestUtility.CreateMinimal(timeProvider: time);
        magnet.Settings.Streaming.MetadataWaitTimeoutSeconds = 0;
        await magnet.StartAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var opening = magnet.Streaming.OpenStreamAsync(0, cancel.Token);
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(opening.IsCompleted);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public async Task Dispose_ForgetsEveryStream()
    {
        await _torrent.StartAsync(TestContext.Current.CancellationToken);
        await using var stream = await _controller.OpenStreamAsync(0, TestContext.Current.CancellationToken);

        _controller.Dispose();

        Assert.False(_controller.IsStreaming);
        Assert.Null(_controller.PriorityPieces);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        _controller.Dispose();
        _controller.Dispose(); // Should not throw
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_DoesNotDisposeActiveStream()
    {
        await _torrent.StartAsync();
        var stream = await _controller.OpenStreamAsync(0);

        _controller.Dispose();

        // Stream should still be usable (controller doesn't own the stream)
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);

        stream.Dispose();
    }

    #endregion

    #region Thread Safety Tests

    [Fact(Timeout = 30000)]
    public async Task ConcurrentAccess_DoesNotThrow()
    {
        await _torrent.StartAsync();

        var tasks = new List<Task>();

        // Multiple concurrent operations
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var stream = await _controller.OpenStreamAsync(0);
                _controller.OnPieceVerified(0);
                await Task.Delay(10);
                stream.Dispose();
            }));

            tasks.Add(Task.Run(() =>
            {
                _torrent.DownloadStrategy = DownloadStrategy.Sequential;
                _ = _torrent.EffectiveDownloadStrategy;
            }));

            tasks.Add(Task.Run(() =>
            {
                _ = _controller.PriorityPieces;
                _ = _controller.IsStreaming;
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_ThreadSafe_WithDispose()
    {
        await _torrent.StartAsync();
        var stream = await _controller.OpenStreamAsync(0);

        var notifyTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                _controller.OnPieceVerified(i % 10);
            }
        });

        var disposeTask = Task.Run(() =>
        {
            Thread.Sleep(5);
            stream.Dispose();
        });

        await Task.WhenAll(notifyTask, disposeTask);
    }

    #endregion

    #region Integration Tests

    [Fact(Timeout = 30000)]
    public async Task FullStreamingWorkflow()
    {
        // Start torrent
        await _torrent.StartAsync();

        // Open stream
        await using var stream = await _controller.OpenStreamAsync(0);

        Assert.Equal(DownloadStrategy.Streaming, _torrent.EffectiveDownloadStrategy);
        Assert.NotNull(_controller.PriorityPieces);
        Assert.Equal(10000, stream.Length);

        // Simulate downloading pieces
        for (int i = 0; i < 10; i++)
        {
            _torrent.Pieces.AddPiece(i);
            _controller.OnPieceVerified(i);
        }

        // Read entire file
        var buffer = new byte[10000];
        int totalRead = 0;
        while (totalRead < 10000)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 10000 - totalRead));
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        Assert.Equal(10000, totalRead);
        Assert.Equal(10000, stream.Position);
    }

    [Fact(Timeout = 30000)]
    public async Task StreamingWithSeek()
    {
        await _torrent.StartAsync();

        // Make all pieces available
        for (int i = 0; i < 10; i++)
        {
            _torrent.Pieces.AddPiece(i);
        }

        await using var stream = await _controller.OpenStreamAsync(0);

        // Read from start
        var buffer = new byte[1000];
        int read = await stream.ReadAsync(buffer.AsMemory(0, 1000));
        Assert.Equal(1000, read);

        // Seek to middle
        stream.Seek(5000, SeekOrigin.Begin);
        Assert.Equal(5000, stream.Position);

        // Read from middle
        read = await stream.ReadAsync(buffer.AsMemory(0, 1000));
        Assert.Equal(1000, read);
        Assert.Equal(6000, stream.Position);

        // Seek to end
        stream.Seek(-500, SeekOrigin.End);
        Assert.Equal(9500, stream.Position);

        // Read remaining
        read = await stream.ReadAsync(buffer.AsMemory(0, 1000));
        Assert.Equal(500, read);
        Assert.Equal(10000, stream.Position);
    }

    #endregion
}





