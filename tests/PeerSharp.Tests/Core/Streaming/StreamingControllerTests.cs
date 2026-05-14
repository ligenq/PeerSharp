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
    public void DownloadStrategy_DefaultsToRarestFirst()
    {
        Assert.Equal(DownloadStrategy.RarestFirst, _controller.DownloadStrategy);
    }

    [Fact]
    public void DownloadStrategy_CanBeSet()
    {
        _controller.DownloadStrategy = DownloadStrategy.Sequential;
        Assert.Equal(DownloadStrategy.Sequential, _controller.DownloadStrategy);

        _controller.DownloadStrategy = DownloadStrategy.Streaming;
        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
    }

    [Fact]
    public void PriorityPieces_DefaultsToNull()
    {
        Assert.Null(_controller.PriorityPieces);
    }

    [Fact]
    public void PriorityPieces_CanBeSet()
    {
        var pieces = new List<int> { 1, 2, 3 };
        _controller.PriorityPieces = pieces;

        Assert.Equal(pieces, _controller.PriorityPieces);
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
    public async Task OpenStreamAsync_SetsActiveStream()
    {
        await _torrent.StartAsync();

        await using var stream = await _controller.OpenStreamAsync(0);

        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
        Assert.NotNull(_controller.PriorityPieces);
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
    public async Task OpenStreamAsync_ReplacesActiveStream()
    {
        await _torrent.StartAsync();

        await using var stream1 = await _controller.OpenStreamAsync(0);
        await using var stream2 = await _controller.OpenStreamAsync(2);

        // Second stream should be active, first is replaced
        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
    }

    #endregion

    #region OnPieceVerified Tests

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
    public async Task OnStreamDisposed_ClearsActiveStream()
    {
        await _torrent.StartAsync();
        var stream = await _controller.OpenStreamAsync(0);

        Assert.NotNull(_controller.PriorityPieces);

        stream.Dispose();

        Assert.Null(_controller.PriorityPieces);
        Assert.Equal(DownloadStrategy.RarestFirst, _controller.DownloadStrategy);
    }

    [Fact(Timeout = 30000)]
    public async Task OnStreamDisposed_OnlyAffectsMatchingStream()
    {
        await _torrent.StartAsync();

        var stream1 = await _controller.OpenStreamAsync(0);
        var stream2 = await _controller.OpenStreamAsync(2);

        // stream2 is now active, stream1 was replaced
        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
        Assert.NotNull(_controller.PriorityPieces);

        // Dispose stream1 (which is no longer the active stream)
        stream1.Dispose();

        // stream2 is still active, so strategy and priorities should still be set
        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
        Assert.NotNull(_controller.PriorityPieces);

        // Now dispose the active stream
        stream2.Dispose();

        // Now strategy should be reset
        Assert.Equal(DownloadStrategy.RarestFirst, _controller.DownloadStrategy);
        Assert.Null(_controller.PriorityPieces);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ClearsActiveStream()
    {
        _controller.PriorityPieces = [1, 2, 3];

        _controller.Dispose();

        // Active stream reference should be cleared (can't directly test, but Dispose shouldn't throw)
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
                _controller.DownloadStrategy = DownloadStrategy.Streaming;
                _ = _controller.DownloadStrategy;
            }));

            tasks.Add(Task.Run(() =>
            {
                _controller.PriorityPieces = [1, 2, 3];
                _ = _controller.PriorityPieces;
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

        Assert.Equal(DownloadStrategy.Streaming, _controller.DownloadStrategy);
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





