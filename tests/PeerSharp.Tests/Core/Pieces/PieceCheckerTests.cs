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
        public List<(long Offset, byte[] Data)> Writes { get; } = new();
        public Dictionary<long, byte[]> Data { get; } = new();
        public HashSet<long> ThrowOnReadOffsets { get; } = new();

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

            if (Data.TryGetValue(offset, out var bytes))
            {
                return Task.FromResult(bytes);
            }

            return Task.FromResult(new byte[length]);
        }

        public Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
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
            return Task.CompletedTask;
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
        public List<byte[]> ExpectedHashes { get; } = new();
        public List<int> VerifiedPieces { get; } = new();
        public List<(int PieceIndex, byte[] PieceData)> VerifyPieceCalls { get; } = new();
        public HashSet<int> MerkleValidPieces { get; } = new();

        public byte[]? GetExpectedHash(int pieceIndex)
        {
            return ExpectedHashes.Count > pieceIndex ? ExpectedHashes[pieceIndex] : null;
        }

        public void UpdatePiecesFromBitfield(byte[] bitfield) { }
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

        public bool VerifyPiece(int pieceIndex, byte[] pieceData)
        {
            VerifyPieceCalls.Add((pieceIndex, pieceData));
            return MerkleValidPieces.Contains(pieceIndex);
        }
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
}






