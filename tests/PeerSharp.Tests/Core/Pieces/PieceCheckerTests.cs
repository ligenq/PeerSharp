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

        public void DeleteFiles() { }

        public Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Writes.Add((offset, data.ToArray()));
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        {
            if (Data.TryGetValue(offset, out var bytes))
            {
                return Task.FromResult(bytes);
            }

            return Task.FromResult(new byte[length]);
        }

        public Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
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
        public List<byte[]> ExpectedHashes { get; } = new();
        public List<int> VerifiedPieces { get; } = new();

        public byte[]? GetExpectedHash(int pieceIndex)
        {
            return ExpectedHashes.Count > pieceIndex ? ExpectedHashes[pieceIndex] : null;
        }

        public void UpdatePiecesFromBitfield(byte[] bitfield) { }
        public void AddPiece(int pieceIndex)
        {
            VerifiedPieces.Add(pieceIndex);
        }

        public bool VerifyPiece(int pieceIndex, byte[] pieceData)
        {
            return true;
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
}






