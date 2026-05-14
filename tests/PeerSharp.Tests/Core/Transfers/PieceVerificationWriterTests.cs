using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Transfers;

public class PieceVerificationWriterTests
{
    private static (Torrent torrent, byte[] data, string tempPath) BuildV1Torrent(int blockCount = 2)
    {
        int pieceSize = ProtocolConstants.BlockSize * blockCount;
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = (uint)pieceSize;
        metadata.Info.FullSize = pieceSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = pieceSize, Offset = 0 });

        byte[] data = new byte[pieceSize];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        metadata.Info.Pieces.Add(SHA1.HashData(data));

        string path = Path.Combine(Path.GetTempPath(), "PeerSharpTests_Verify", Guid.NewGuid().ToString("N"));
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        return (torrent, data, path);
    }

    private static PieceState BuildPieceState(byte[] data, int blockCount = 2)
    {
        var piece = new PieceState(0, blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            var block = new Block(0, i * ProtocolConstants.BlockSize, ProtocolConstants.BlockSize);
            data.AsSpan(i * ProtocolConstants.BlockSize, ProtocolConstants.BlockSize).CopyTo(block.Buffer);
            piece.BlockData[i] = block;
        }
        return piece;
    }


    [Fact]
    public async Task VerifyAndWriteAsync_WritesVerifiedPieceToDisk()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize * 2;
        metadata.Info.FullSize = metadata.Info.PieceSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        byte[] data = new byte[metadata.Info.PieceSize];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }
        metadata.Info.Pieces.Add(SHA1.HashData(data));

        string path = Path.Combine(Path.GetTempPath(), "PeerSharpTests_Verify", Guid.NewGuid().ToString("N"));
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        var selection = new List<FileSelection>
        {
            new FileSelection { Selected = true, Priority = Priority.Normal }
        };
        await torrent.FilesInternal.InitializeAsync(selection, CancellationToken.None);

        var writer = new PieceVerificationWriter(
            torrent,
            TimeProvider.System,
            NullLogger<PieceVerificationWriter>.Instance,
            ProtocolConstants.BlockSize,
            _ => { });

        var piece = new PieceState(0, 2);
        piece.BlockData[0] = new Block(0, 0, ProtocolConstants.BlockSize);
        piece.BlockData[1] = new Block(0, ProtocolConstants.BlockSize, ProtocolConstants.BlockSize);
        data.AsSpan(0, ProtocolConstants.BlockSize).CopyTo(piece.BlockData[0]!.Buffer);
        data.AsSpan(ProtocolConstants.BlockSize, ProtocolConstants.BlockSize).CopyTo(piece.BlockData[1]!.Buffer);

        var outcome = await writer.VerifyAsync(piece, CancellationToken.None);
        Assert.True(outcome.HashSuccess);
        Assert.False(outcome.HashFailed);

        bool writeOk = await writer.WriteAsync(piece, outcome.PieceSize, outcome.FullData, CancellationToken.None);
        Assert.True(writeOk);

        byte[] onDisk = await torrent.FilesInternal.ReadAsync(0, data.Length, CancellationToken.None);
        Assert.Equal(data, onDisk);

        piece.Dispose();
        await torrent.DisposeAsync();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task VerifyAsync_WrongHash_ReturnsHashFailed()
    {
        var (torrent, data, path) = BuildV1Torrent();
        var selection = new List<FileSelection> { new FileSelection { Selected = true, Priority = Priority.Normal } };
        await torrent.FilesInternal.InitializeAsync(selection, CancellationToken.None);

        var writer = new PieceVerificationWriter(torrent, TimeProvider.System, NullLogger<PieceVerificationWriter>.Instance, ProtocolConstants.BlockSize, _ => { });

        // Corrupt the data so the hash won't match.
        var corruptData = (byte[])data.Clone();
        corruptData[0] ^= 0xFF;
        var piece = BuildPieceState(corruptData);

        using var outcome = await writer.VerifyAsync(piece, CancellationToken.None);

        Assert.False(outcome.HashSuccess);
        Assert.True(outcome.HashFailed);

        piece.Dispose();
        await torrent.DisposeAsync();
        try { Directory.Delete(path, true); } catch { }
    }

    [Fact]
    public async Task VerifyAsync_NullBlock_ReturnsHashFailed()
    {
        var (torrent, _, path) = BuildV1Torrent();
        var selection = new List<FileSelection> { new FileSelection { Selected = true, Priority = Priority.Normal } };
        await torrent.FilesInternal.InitializeAsync(selection, CancellationToken.None);

        var writer = new PieceVerificationWriter(torrent, TimeProvider.System, NullLogger<PieceVerificationWriter>.Instance, ProtocolConstants.BlockSize, _ => { });

        // Leave BlockData[1] null to simulate an incomplete piece.
        var piece = new PieceState(0, 2);
        piece.BlockData[0] = new Block(0, 0, ProtocolConstants.BlockSize);

        using var outcome = await writer.VerifyAsync(piece, CancellationToken.None);

        Assert.False(outcome.HashSuccess);
        Assert.True(outcome.HashFailed);

        piece.BlockData[0]!.Dispose();
        piece.Dispose();
        await torrent.DisposeAsync();
        try { Directory.Delete(path, true); } catch { }
    }

    [Fact]
    public async Task WriteAsync_WriteThrows_ReturnsFalse()
    {
        var (torrent, data, path) = BuildV1Torrent();
        var selection = new List<FileSelection> { new FileSelection { Selected = true, Priority = Priority.Normal } };
        await torrent.FilesInternal.InitializeAsync(selection, CancellationToken.None);

        var writer = new PieceVerificationWriter(torrent, TimeProvider.System, NullLogger<PieceVerificationWriter>.Instance, ProtocolConstants.BlockSize, _ => { });
        var piece = BuildPieceState(data);

        // Pass null fullData with non-zero pieceSize to trigger ArgumentOutOfRangeException.
        bool result = await writer.WriteAsync(piece, data.Length, null, CancellationToken.None);

        Assert.False(result);

        piece.Dispose();
        await torrent.DisposeAsync();
        try { Directory.Delete(path, true); } catch { }
    }

    [Fact]
    public async Task PieceVerificationOutcome_Dispose_ReturnsBufferOnce()
    {
        var (torrent, data, path) = BuildV1Torrent();
        var selection = new List<FileSelection> { new FileSelection { Selected = true, Priority = Priority.Normal } };
        await torrent.FilesInternal.InitializeAsync(selection, CancellationToken.None);

        var writer = new PieceVerificationWriter(torrent, TimeProvider.System, NullLogger<PieceVerificationWriter>.Instance, ProtocolConstants.BlockSize, _ => { });
        var piece = BuildPieceState(data);

        var outcome = await writer.VerifyAsync(piece, CancellationToken.None);
        Assert.NotNull(outcome.FullData);

        outcome.Dispose();
        outcome.Dispose(); // Double-dispose must not throw.

        piece.Dispose();
        await torrent.DisposeAsync();
        try { Directory.Delete(path, true); } catch { }
    }
}
