using PeerSharp.Internals;
using PeerSharp.Streaming;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Streaming;

public class TorrentStreamTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider = new();

    public TorrentStreamTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        // Setup a file: 10KB total, 1KB pieces
        _torrent.InfoFile.Info.PieceSize = 1000;
        _torrent.InfoFile.Info.FullSize = 10000;
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "stream.dat", Size = 10000, Offset = 0 });
        _torrent.InfoFile.Info.Pieces.Clear();
        for (int i = 0; i < 10; i++)
        {
            _torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        }

        _torrent.ReinitializeAfterMetadataAsync().GetAwaiter().GetResult();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_SetsStreamingMode_AndInitialPriorities()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        Assert.Equal(DownloadStrategy.Streaming, _torrent.DownloadStrategy);
        Assert.NotNull(_torrent.StreamingPriorityPieces);

        // Should prioritize start (header) pieces
        Assert.Contains(0, _torrent.StreamingPriorityPieces);
    }

    [Fact]
    public void Constructor_ThrowsForInvalidFileIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TorrentStream(_torrent.Streaming, _torrent, -1, _timeProvider));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TorrentStream(_torrent.Streaming, _torrent, 99, _timeProvider));
    }

    [Fact]
    public void Constructor_PrioritizesHeaderAndFooterPieces()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        var priorities = _torrent.StreamingPriorityPieces!;

        // Header pieces (first 3) should be prioritized
        Assert.Contains(0, priorities);
        Assert.Contains(1, priorities);
        Assert.Contains(2, priorities);

        // Footer piece (last 1MB = piece 9) should be prioritized
        Assert.Contains(9, priorities);
    }

    #endregion

    #region Stream Properties Tests

    [Fact]
    public void StreamProperties_AreCorrect()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(10000, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Position_CanBeSetViaProperty()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        stream.Position = 5000;

        Assert.Equal(5000, stream.Position);
    }

    #endregion

    #region Seek Tests

    [Fact]
    public void Seek_UpdatesPriorities()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        // Seek to middle (Piece 5)
        stream.Seek(5000, SeekOrigin.Begin);

        Assert.Equal(5000, stream.Position);

        // Priorities should now include piece 5
        Assert.Contains(5, _torrent.StreamingPriorityPieces!);
    }

    [Fact]
    public void Seek_FromBegin_SetsAbsolutePosition()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        long result = stream.Seek(3000, SeekOrigin.Begin);

        Assert.Equal(3000, result);
        Assert.Equal(3000, stream.Position);
    }

    [Fact]
    public void Seek_FromCurrent_AddsToPosition()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        stream.Seek(2000, SeekOrigin.Begin);

        long result = stream.Seek(1500, SeekOrigin.Current);

        Assert.Equal(3500, result);
        Assert.Equal(3500, stream.Position);
    }

    [Fact]
    public void Seek_FromEnd_SubtractsFromLength()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        long result = stream.Seek(-1000, SeekOrigin.End);

        Assert.Equal(9000, result);
        Assert.Equal(9000, stream.Position);
    }

    [Fact]
    public void Seek_ClampsToValidRange()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        // Seek before start
        Assert.Equal(0, stream.Seek(-100, SeekOrigin.Begin));

        // Seek past end
        Assert.Equal(10000, stream.Seek(20000, SeekOrigin.Begin));
    }

    [Fact]
    public void Seek_ToSamePosition_DoesNotUpdatePriorities()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        stream.Seek(5000, SeekOrigin.Begin);
        var originalPriorities = _torrent.StreamingPriorityPieces;

        // Seek to same position
        stream.Seek(5000, SeekOrigin.Begin);

        // Should be same reference (no update)
        Assert.Same(originalPriorities, _torrent.StreamingPriorityPieces);
    }

    #endregion

    #region Read Tests

    [Fact]
    public void Read_Synchronous_ThrowsNotSupported()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        var buffer = new byte[100];

        Assert.Throws<NotSupportedException>(() => stream.Read(buffer, 0, 100));
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReturnsZero_WhenAtEnd()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        stream.Seek(10000, SeekOrigin.Begin);

        var buffer = new byte[100];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(buffer.AsMemory(0, 100), cts.Token);

        Assert.Equal(0, read);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_BlocksUntilDataAvailable()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        // Start read task (should block because no pieces are available)
        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100);

        // Verify it's not completed immediately
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        // Simulate piece downloaded (Piece 0 covers 0-1000)
        _torrent.Pieces.AddPiece(0);
        _torrent.OnPieceVerified(0);

        // Should complete now
        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(100, read);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReturnsPartialData_WhenOnlySomePiecesAvailable()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        // Make only piece 0 available (covers bytes 0-999)
        _torrent.Pieces.AddPiece(0);

        var buffer = new byte[2000]; // Request 2000 bytes spanning pieces 0 and 1
        int read = await stream.ReadAsync(buffer.AsMemory(0, 2000));

        // Should only read what's available (1000 bytes from piece 0)
        Assert.Equal(1000, read);
        Assert.Equal(1000, stream.Position);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReadsAcrossMultiplePieces()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        // Make pieces 0, 1, 2 available
        _torrent.Pieces.AddPiece(0);
        _torrent.Pieces.AddPiece(1);
        _torrent.Pieces.AddPiece(2);

        var buffer = new byte[2500];
        int read = await stream.ReadAsync(buffer.AsMemory(0, 2500));

        Assert.Equal(2500, read);
        Assert.Equal(2500, stream.Position);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReturnsZero_WhenCancelled()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        using var cts = new CancellationTokenSource();

        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100, cts.Token);

        // Cancel immediately
        cts.Cancel();

        int read = await readTask;
        Assert.Equal(0, read);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_UpdatesPosition()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        _torrent.Pieces.AddPiece(0);

        var buffer = new byte[500];
        Assert.Equal(500, await stream.ReadAsync(buffer.AsMemory(0, 500)));
        Assert.Equal(500, stream.Position);

        Assert.Equal(300, await stream.ReadAsync(buffer.AsMemory(0, 300)));
        Assert.Equal(800, stream.Position);
    }

    #endregion

    #region Timeout Tests

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_TimesOut_AfterMaxWait()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100);

        // Advance time past the 60 second timeout
        _timeProvider.Advance(TimeSpan.FromSeconds(61));

        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
    }

    #endregion

    #region OnPieceVerified Tests

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_SignalsWaitingReader()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100);

        // Add piece and notify
        _torrent.Pieces.AddPiece(0);
        stream.OnPieceVerified(0);

        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(100, read);
    }

    [Fact(Timeout = 30000)]
    public async Task OnPieceVerified_DoesNotSignal_ForIrrelevantPieces()
    {
        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        var buffer = new byte[100];
        var readTask = stream.ReadAsync(buffer, 0, 100);

        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        // Notify about irrelevant piece (piece 9, we're reading from 0)
        stream.OnPieceVerified(9);

        // Should still be waiting
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        // Now add the relevant piece
        _torrent.Pieces.AddPiece(0);
        stream.OnPieceVerified(0);

        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(100, read);
    }

    #endregion

    #region Dispose Tests

    [Fact(Timeout = 30000)]
    public async Task Dispose_ResetsStrategyAndClearsPriorities()
    {
        await _torrent.StartAsync();
        var stream = await _torrent.Streaming.OpenStreamAsync(0);
        Assert.Equal(DownloadStrategy.Streaming, _torrent.DownloadStrategy);
        Assert.NotNull(_torrent.StreamingPriorityPieces);

        stream.Dispose();

        Assert.Equal(DownloadStrategy.RarestFirst, _torrent.DownloadStrategy);
        Assert.Null(_torrent.StreamingPriorityPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        await _torrent.StartAsync();
        var stream = await _torrent.Streaming.OpenStreamAsync(0);

        stream.Dispose();
        stream.Dispose(); // Should not throw
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_NotifiesController()
    {
        await _torrent.StartAsync();
        var stream = await _torrent.Streaming.OpenStreamAsync(0);
        Assert.NotNull(_torrent.Streaming.PriorityPieces);

        stream.Dispose();

        // Controller should no longer have an active stream
        Assert.Null(_torrent.Streaming.PriorityPieces);
    }

    #endregion

    #region Write Tests

    [Fact]
    public void Write_ThrowsNotSupported()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);
        var buffer = new byte[100];

        Assert.Throws<NotSupportedException>(() => stream.Write(buffer, 0, 100));
    }

    [Fact]
    public void SetLength_ThrowsNotSupported()
    {
        using var stream = new TorrentStream(_torrent.Streaming, _torrent, 0, _timeProvider);

        Assert.Throws<NotSupportedException>(() => stream.SetLength(5000));
    }

    #endregion

    #region Multi-File Torrent Tests

    [Fact]
    public async Task Constructor_CalculatesCorrectOffset_ForSecondFile()
    {
        // Add a second file
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "stream2.dat", Size = 5000, Offset = 10000 });
        _torrent.InfoFile.Info.FullSize = 15000;
        for (int i = 0; i < 5; i++)
        {
            _torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        }

        await _torrent.ReinitializeAfterMetadataAsync();

        await using var stream = new TorrentStream(_torrent.Streaming, _torrent, 1, _timeProvider);

        Assert.Equal(5000, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    #endregion
}





