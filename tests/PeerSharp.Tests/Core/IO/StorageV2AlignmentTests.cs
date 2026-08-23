using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// Where a v2 torrent's bytes actually land on disk.
///
/// <para>
/// This is the test the corruption needed. BEP 52 starts every file on a piece boundary, so an offset
/// in the piece space is not an offset into the concatenated files - they differ by the padding after
/// any file that does not end on a boundary. Storage laid its files out along their sizes, so every
/// file after the first was written earlier than it should have been by the accumulated gap.
/// </para>
/// <para>
/// Nothing caught it, because a piece is verified when it is assembled and before it is written: the
/// hashes all passed, the download reported one hundred per cent, and the files on disk were wrong
/// from the second one onwards. Only a byte-for-byte comparison against the source finds that, which
/// is what this does.
/// </para>
/// </summary>
public class StorageV2AlignmentTests : IAsyncLifetime
{
    private const uint PieceSize = 16384;

    /// <summary>Deliberately not a multiple of the piece size, so each file leaves a gap behind it.</summary>
    private const int FileSize = (int)PieceSize + 4000;

    private readonly FileHandleCache _handleCache = new();
    private readonly string _tempDir;

    public StorageV2AlignmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "StorageV2Align", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _handleCache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EachFileIsWrittenAtThePieceBoundaryItStartsOn()
    {
        var metadata = CreateAlignedV2(fileCount: 3);
        var payloads = new Dictionary<int, byte[]>();

        // Scoped so every handle is closed before the files are read back off the disk.
        await using (var storage = new Storage(metadata, _tempDir, new PathValidator(_tempDir), _handleCache, enableSparseFiles: false))
        {
            await storage.InitAsync(ct: TestContext.Current.CancellationToken);

            // One distinctive payload per file, written at the offset the piece space gives that file.
            for (int i = 0; i < metadata.Info.Files.Count; i++)
            {
                byte[] payload = new byte[FileSize];
                Array.Fill(payload, (byte)(0xA0 + i));
                payloads[i] = payload;

                await storage.WriteAsync(metadata.Info.Files[i].Offset, payload, TestContext.Current.CancellationToken);
            }

            await storage.FlushAsync(TestContext.Current.CancellationToken);
        }

        _handleCache.CloseTorrentHandles(_tempDir);

        // Read each file off the disk directly. Laying the files out along their sizes rather than
        // their piece-space spans puts file 1 four thousand bytes early, and file 2 eight thousand.
        for (int i = 0; i < metadata.Info.Files.Count; i++)
        {
            string path = Path.Combine(_tempDir, metadata.Info.Files[i].Path);

            Assert.True(File.Exists(path), $"file {i} should exist");
            byte[] onDisk = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(FileSize, onDisk.Length);
            Assert.Equal(payloads[i], onDisk);
        }
    }

    [Fact]
    public async Task APieceSpaceOffsetReadsBackWhatWasWrittenThere()
    {
        // The same property from storage's own side, which is what the engine relies on when it
        // re-reads a piece to seed it.
        var metadata = CreateAlignedV2(fileCount: 3);
        await using var storage = new Storage(metadata, _tempDir, new PathValidator(_tempDir), _handleCache, enableSparseFiles: false);
        await storage.InitAsync(ct: TestContext.Current.CancellationToken);

        long lastFileOffset = metadata.Info.Files[^1].Offset;
        byte[] written = new byte[2048];
        Random.Shared.NextBytes(written);

        await storage.WriteAsync(lastFileOffset, written, TestContext.Current.CancellationToken);
        await storage.FlushAsync(TestContext.Current.CancellationToken);

        byte[] read = await storage.ReadAsync(lastFileOffset, written.Length, TestContext.Current.CancellationToken);

        Assert.Equal(written, read);
    }

    private static TorrentFileMetadata CreateAlignedV2(int fileCount)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "v2";
        metadata.Info.PieceSize = PieceSize;
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();

        long offset = 0;
        for (int i = 0; i < fileCount; i++)
        {
            metadata.Info.Files.Add(new Internals.TorrentFileEntry
            {
                Path = $"file{i}.bin",
                Size = FileSize,
                Offset = offset,
                FirstPieceIndex = (int)(offset / PieceSize),
                PieceCount = (int)((FileSize + PieceSize - 1) / PieceSize),
                PiecesRoot = new byte[32]
            });

            offset += (FileSize + PieceSize - 1) / PieceSize * PieceSize;
        }

        metadata.Info.FullSize = offset;
        return metadata;
    }
}
