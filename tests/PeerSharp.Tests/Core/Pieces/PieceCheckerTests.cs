using PeerSharp.PiecePicking;
using PeerSharp.PieceWriter;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Pieces;

public class PieceCheckerTests
{
    private class MockFiles : IInternalFiles
    {
        public bool Checking { get; set; }
        public string DownloadPath => "";
        public List<(long Offset, byte[] Data)> Writes { get; } = [];
        public Dictionary<long, byte[]> Data { get; } = [];
        public HashSet<long> ThrowOnReadOffsets { get; } = [];

        // When true, each ReadAsync yields to the task scheduler before returning,
        // making CheckAllPiecesAsync truly asynchronous.
        public bool YieldOnRead { get; set; }

        // When set, each ReadAsync (Memory<byte> overload) must acquire one permit before
        // reading. Use to prevent the check from completing before a specific test action.
        public SemaphoreSlim? ReadGate { get; set; }

        public void DeleteFiles() { }

        public Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Writes.Add((offset, data.ToArray()));
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        {
            if (ThrowOnReadOffsets.Contains(offset))
            {
                throw new IOException("read failed");
            }

            var bytes = Data.TryGetValue(offset, out var d) ? d : new byte[length];
            return Task.FromResult(bytes);
        }

        public async Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            if (YieldOnRead)
            {
                await Task.Yield();
            }

            if (ReadGate != null)
            {
                await ReadGate.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();

            if (ThrowOnReadOffsets.Contains(offset))
            {
                throw new IOException("read failed");
            }

            if (Data.TryGetValue(offset, out var bytes))
            {
                bytes.CopyTo(buffer);
            }
            else
            {
                buffer.Span.Clear();
            }
        }
    }


    private class MockContext : IPieceCheckerContext
    {
        public int PieceCount { get; set; }
        public long PieceSize { get; set; }
        public long FullSize { get; set; }
        public string TorrentName => "test";
        public bool IsMerkle { get; set; }
        public bool IsV2 { get; set; }
        public List<byte[]> ExpectedHashes { get; } = [];
        public List<int> VerifiedPieces { get; } = [];
        public List<(int PieceIndex, byte[] PieceData)> VerifyPieceCalls { get; } = [];
        public HashSet<int> MerkleValidPieces { get; } = [];
        public Action<byte[]>? BitfieldCallback { get; set; }

        public byte[]? GetExpectedHash(int pieceIndex)
        {
            return ExpectedHashes.Count > pieceIndex ? ExpectedHashes[pieceIndex] : null;
        }

        public void UpdatePiecesFromBitfield(byte[] bitfield) => BitfieldCallback?.Invoke(bitfield);
        public long GetPieceSize(int pieceIndex)
        {
            if (pieceIndex == PieceCount - 1)
            {
                long lastPieceSize = FullSize % PieceSize;
                return lastPieceSize == 0 ? PieceSize : lastPieceSize;
            }

            return PieceSize;
        }

        public void AddPiece(int pieceIndex)
        {
            VerifiedPieces.Add(pieceIndex);
        }

        public bool VerifyPiece(int pieceIndex, ReadOnlySpan<byte> pieceData)
        {
            VerifyPieceCalls.Add((pieceIndex, pieceData.ToArray()));
            return MerkleValidPieces.Contains(pieceIndex);
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_StandardHashes_VerifiesCorrectly()
    {
        // Arrange
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 2,
            PieceSize = 10,
            FullSize = 20
        };

        byte[] piece0 = new byte[10]; piece0[0] = 1;
        byte[] piece1 = new byte[10]; piece1[0] = 2;

        files.Data[0] = piece0;
        files.Data[10] = piece1;

        ctx.ExpectedHashes.Add(SHA1.HashData(piece0));
        ctx.ExpectedHashes.Add(SHA1.HashData(piece1));

        var checker = new PieceChecker(files, ctx);

        // Act
        int valid = await checker.CheckAllPiecesAsync();

        // Assert
        Assert.Equal(2, valid);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_InvalidPiece_ReturnsCorrectCount()
    {
        // Arrange
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 1,
            PieceSize = 10,
            FullSize = 10
        };

        files.Data[0] = new byte[10]; // All zeros
        ctx.ExpectedHashes.Add(new byte[20]); // Expected hash is also different (zeros won't match SHA1 of zeros? Wait, SHA1 of 10 zero bytes is not 20 zero bytes)

        var checker = new PieceChecker(files, ctx);

        // Act
        int valid = await checker.CheckAllPiecesAsync();

        // Assert
        Assert.Equal(0, valid);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPieceRangeAsync_StandardHashes_VerifiesRequestedRange()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 4,
            PieceSize = 10,
            FullSize = 40
        };

        for (int i = 0; i < ctx.PieceCount; i++)
        {
            byte[] piece = new byte[10];
            piece[0] = (byte)(i + 1);
            files.Data[i * 10] = piece;
            ctx.ExpectedHashes.Add(SHA1.HashData(piece));
        }

        var checker = new PieceChecker(files, ctx);

        int valid = await checker.CheckPieceRangeAsync(1, 2);

        Assert.Equal(2, valid);
        Assert.Equal(new[] { 1, 2 }, ctx.VerifiedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPieceRangeAsync_StopsAtPieceCount()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 2,
            PieceSize = 10,
            FullSize = 20
        };

        for (int i = 0; i < ctx.PieceCount; i++)
        {
            byte[] piece = new byte[10];
            piece[0] = (byte)(i + 1);
            files.Data[i * 10] = piece;
            ctx.ExpectedHashes.Add(SHA1.HashData(piece));
        }

        var checker = new PieceChecker(files, ctx);

        int valid = await checker.CheckPieceRangeAsync(0, 5);

        Assert.Equal(2, valid);
        Assert.Equal(new[] { 0, 1 }, ctx.VerifiedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPieceRangeAsync_MissingExpectedHash_DoesNotAddPiece()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 1,
            PieceSize = 10,
            FullSize = 10
        };

        files.Data[0] = new byte[10];
        var checker = new PieceChecker(files, ctx);

        int valid = await checker.CheckPieceRangeAsync(0, 0);

        Assert.Equal(0, valid);
        Assert.Empty(ctx.VerifiedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPieceRangeAsync_MerkleContext_UsesContextVerification()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 2,
            PieceSize = 10,
            FullSize = 20,
            IsMerkle = true
        };
        ctx.MerkleValidPieces.Add(1);
        files.Data[0] = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        files.Data[10] = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        var checker = new PieceChecker(files, ctx);

        int valid = await checker.CheckPieceRangeAsync(0, 1);

        Assert.Equal(1, valid);
        Assert.Equal(new[] { 1 }, ctx.VerifiedPieces);
        Assert.Equal(new[] { 0, 1 }, ctx.VerifyPieceCalls.Select(call => call.PieceIndex));
    }

    [Fact(Timeout = 30000)]
    public async Task CheckPieceRangeAsync_ReadFailure_SkipsPiece()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 2,
            PieceSize = 10,
            FullSize = 20
        };

        byte[] piece0 = new byte[10];
        piece0[0] = 1;
        byte[] piece1 = new byte[10];
        piece1[0] = 2;
        files.Data[0] = piece0;
        files.Data[10] = piece1;
        files.ThrowOnReadOffsets.Add(10);
        ctx.ExpectedHashes.Add(SHA1.HashData(piece0));
        ctx.ExpectedHashes.Add(SHA1.HashData(piece1));

        var checker = new PieceChecker(files, ctx);

        int valid = await checker.CheckPieceRangeAsync(0, 1);

        Assert.Equal(1, valid);
        Assert.Equal(new[] { 0 }, ctx.VerifiedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_AlreadyRunning_ThrowsInvalidOperationException()
    {
        var gate = new SemaphoreSlim(0);
        var files = new MockFiles { YieldOnRead = true, ReadGate = gate };
        var ctx = new MockContext { PieceCount = 100, PieceSize = 10, FullSize = 1000 };
        for (int i = 0; i < ctx.PieceCount; i++)
        {
            ctx.ExpectedHashes.Add(SHA1.HashData(new byte[10]));
        }

        var checker = new PieceChecker(files, ctx);

        var first = checker.CheckAllPiecesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => checker.CheckAllPiecesAsync());
        gate.Release(ctx.PieceCount);
        await first;
    }

    [Fact(Timeout = 30000)]
    public async Task IsRunning_TrueWhileChecking_FalseAfter()
    {
        var files = new MockFiles { YieldOnRead = true };
        var ctx = new MockContext { PieceCount = 1, PieceSize = 10, FullSize = 10 };
        ctx.ExpectedHashes.Add(SHA1.HashData(new byte[10]));

        var checker = new PieceChecker(files, ctx);

        Assert.False(checker.IsRunning);
        var task = checker.CheckAllPiecesAsync();
        Assert.True(checker.IsRunning);
        await task;
        Assert.False(checker.IsRunning);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancel_WhileRunning_StopsBeforeAllPieces()
    {
        const int pieceCount = 200;
        var files = new MockFiles();
        var ctx = new MockContext { PieceCount = pieceCount, PieceSize = 10, FullSize = pieceCount * 10 };
        for (int i = 0; i < pieceCount; i++)
        {
            byte[] piece = new byte[10]; piece[0] = (byte)(i % 256);
            files.Data[i * 10] = piece;
            ctx.ExpectedHashes.Add(SHA1.HashData(piece));
        }

        var checker = new PieceChecker(files, ctx);
        var task = checker.CheckAllPiecesAsync();
        checker.Cancel();
        await task;

        // Cancelled early — fewer than all pieces verified.
        Assert.True(ctx.VerifiedPieces.Count < pieceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_WithProgress_ReportsProgressToCallback()
    {
        const int pieceCount = 3;
        var files = new MockFiles();
        var ctx = new MockContext { PieceCount = pieceCount, PieceSize = 10, FullSize = pieceCount * 10 };
        for (int i = 0; i < pieceCount; i++)
        {
            byte[] piece = new byte[10]; piece[0] = (byte)(i + 1);
            files.Data[i * 10] = piece;
            ctx.ExpectedHashes.Add(SHA1.HashData(piece));
        }

        var reports = new List<PieceCheckProgress>();
        IProgress<PieceCheckProgress> progress = new SyncProgress<PieceCheckProgress>(p => reports.Add(p));

        var checker = new PieceChecker(files, ctx, progress);
        await checker.CheckAllPiecesAsync();

        // One progress report per piece.
        Assert.Equal(pieceCount, reports.Count);
        Assert.Equal(pieceCount, reports[^1].TotalPieces);
        Assert.Equal(pieceCount, reports[^1].CheckedPieces);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeAsync_CancelsRunningCheck()
    {
        const int pieceCount = 500;
        var files = new MockFiles();
        var ctx = new MockContext { PieceCount = pieceCount, PieceSize = 10, FullSize = pieceCount * 10 };
        for (int i = 0; i < pieceCount; i++)
        {
            ctx.ExpectedHashes.Add(SHA1.HashData(new byte[10]));
        }

        var checker = new PieceChecker(files, ctx);
        var task = checker.CheckAllPiecesAsync();
        await checker.DisposeAsync();
        await task;

        Assert.True(ctx.VerifiedPieces.Count < pieceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_V2Context_UsesContextVerification()
    {
        var files = new MockFiles();
        var ctx = new MockContext
        {
            PieceCount = 2,
            PieceSize = 10,
            FullSize = 20,
            IsV2 = true
        };
        ctx.MerkleValidPieces.Add(0);
        files.Data[0] = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        files.Data[10] = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        var checker = new PieceChecker(files, ctx);
        int valid = await checker.CheckAllPiecesAsync();

        Assert.Equal(1, valid);
        Assert.Equal(2, ctx.VerifyPieceCalls.Count);
        Assert.Equal(new[] { 0, 1 }, ctx.VerifyPieceCalls.Select(c => c.PieceIndex).ToArray());
    }

    [Fact(Timeout = 30000)]
    public async Task CheckAllPiecesAsync_UpdatesBitfieldOnContext()
    {
        var files = new MockFiles();
        var ctx = new MockContext { PieceCount = 2, PieceSize = 10, FullSize = 20 };

        byte[] piece0 = new byte[10]; piece0[0] = 1;
        byte[] piece1 = new byte[10]; piece1[0] = 2;
        files.Data[0] = piece0;
        files.Data[10] = piece1;
        ctx.ExpectedHashes.Add(SHA1.HashData(piece0));
        ctx.ExpectedHashes.Add(SHA1.HashData(piece1));

        byte[]? capturedBitfield = null;
        ctx.BitfieldCallback = b => capturedBitfield = b;

        var checker = new PieceChecker(files, ctx);
        await checker.CheckAllPiecesAsync();

        Assert.NotNull(capturedBitfield);
    }
}






