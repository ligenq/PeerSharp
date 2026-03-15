using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Transfers;

public class PieceVerificationWriterTests
{
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
}
