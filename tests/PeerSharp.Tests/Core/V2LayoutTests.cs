using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

/// <summary>
/// How a v2 torrent's files sit in its piece space, and what of that is content.
///
/// <para>
/// BEP 52 starts every file on a piece boundary, so a v2 torrent's piece space is larger than the sum
/// of its files by whatever padding that alignment implies. Getting the two confused is silent in a
/// way worth stating: verification happens on the assembled piece before it is written, so a torrent
/// whose files are laid out at the wrong offsets still passes every hash check, reports one hundred
/// per cent, and leaves the first file correct and every later one corrupt.
/// </para>
/// </summary>
public class V2LayoutTests
{
    private const uint PieceSize = 64 * 1024;

    [Fact]
    public void V2FilesOccupyWholePiecesEvenWhenTheyDoNotFillThem()
    {
        // Three files of one and a half pieces each. Every one starts on a piece boundary, so each
        // occupies two whole pieces of the piece space and leaves half a piece of padding behind.
        var info = CreateV2(fileSize: (long)(PieceSize + (PieceSize / 2)), fileCount: 3);

        var spans = info.GetPieceSpaceSpans();

        Assert.Equal([2 * PieceSize, 2 * PieceSize, 2 * PieceSize], spans);
    }

    [Fact]
    public void ThePieceSpaceSpansReproduceTheOffsetsTheParserRecorded()
    {
        // This is the property storage depends on: laying the files out end to end along these spans
        // has to put each file exactly where its recorded offset says it is. Laying them out along
        // their sizes instead is what corrupted every file after the first.
        var info = CreateV2(fileSize: (long)PieceSize + 1000, fileCount: 4);

        var spans = info.GetPieceSpaceSpans();

        long offset = 0;
        for (int i = 0; i < info.Files.Count; i++)
        {
            Assert.Equal(info.Files[i].Offset, offset);
            offset += spans[i];
        }

        Assert.Equal(info.FullSize, offset);
    }

    [Fact]
    public void AV1TorrentsSpansAreSimplyItsFileSizes()
    {
        // v1 packs files end to end, so nothing here may change for it.
        var info = new Internals.TorrentFileInfo { PieceSize = PieceSize, Version = TorrentVersion.V1 };
        long offset = 0;
        foreach (long size in new long[] { 1000, 70000, 3 })
        {
            info.Files.Add(new Internals.TorrentFileEntry { Path = $"f{offset}", Size = size, Offset = offset });
            offset += size;
        }

        info.FullSize = offset;

        Assert.Equal([1000L, 70000L, 3L], info.GetPieceSpaceSpans());
    }

    [Fact]
    public void ContentSizeIsTheFilesRatherThanThePieceSpace()
    {
        long fileSize = PieceSize + 1000;
        var info = CreateV2(fileSize, fileCount: 3);

        Assert.Equal(3 * fileSize, info.ContentSize);

        // The piece space is bigger, and reporting it as the torrent's size overstates it by the
        // padding and leaves the remaining-bytes count unable to reach zero.
        Assert.True(info.FullSize > info.ContentSize);
    }

    [Fact]
    public void ContentSizeExcludesExplicitPaddingFiles()
    {
        // The v1 spelling of the same idea: BEP 47 padding entries are layout, not content.
        var info = new Internals.TorrentFileInfo { PieceSize = PieceSize, Version = TorrentVersion.V1 };
        info.Files.Add(new Internals.TorrentFileEntry { Path = "real.bin", Size = 1000, Offset = 0 });
        info.Files.Add(new Internals.TorrentFileEntry { Path = ".pad/500", Size = 500, Offset = 1000, IsPadding = true });
        info.FullSize = 1500;

        Assert.Equal(1000, info.ContentSize);
    }

    [Fact]
    public async Task AV2TorrentKnowsItHasMetadata()
    {
        // A v2 torrent has no SHA-1 pieces string and no merkle root; it describes its pieces in the
        // file tree. Asking only for the first two left it permanently "without metadata", which the
        // download path ignored and the completion check did not - so it finished and could never say
        // so.
        var metadata = new TorrentFileMetadata { Info = CreateV2(PieceSize, fileCount: 2) };
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        try
        {
            Assert.True(torrent.HasMetadata);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task PaddingDoesNotCountAsFinishedContent()
    {
        // Each one-byte file occupies a whole piece in v2. Completing the first piece must report
        // one byte finished and one byte left, not one whole piece finished and nothing left.
        var metadata = new TorrentFileMetadata { Info = CreateV2(fileSize: 1, fileCount: 2) };
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        try
        {
            torrent.Pieces.AddPiece(0);

            Assert.Equal(1UL, torrent.FinishedBytes);
            Assert.Equal(1, torrent.DataLeft);

            torrent.Pieces.AddPiece(1);

            Assert.Equal(2UL, torrent.FinishedBytes);
            Assert.Equal(0, torrent.DataLeft);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnEmptyV2TorrentStillHasMetadataAndIsFinished()
    {
        var metadata = new TorrentFileMetadata { Info = CreateV2(fileSize: 0, fileCount: 1) };
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        try
        {
            Assert.True(torrent.HasMetadata);
            Assert.True(torrent.Finished);
            Assert.Equal(0, torrent.TotalSize);
            Assert.Equal(0, torrent.DataLeft);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    private static Internals.TorrentFileInfo CreateV2(long fileSize, int fileCount)
    {
        var info = new Internals.TorrentFileInfo
        {
            PieceSize = PieceSize,
            Version = TorrentVersion.V2,
            HashV2 = InfoHash.CreateRandomV2(),
            Name = "v2"
        };

        long offset = 0;
        for (int i = 0; i < fileCount; i++)
        {
            info.Files.Add(new Internals.TorrentFileEntry
            {
                Path = $"file{i}.bin",
                Size = fileSize,
                Offset = offset,
                FirstPieceIndex = (int)(offset / PieceSize),
                PieceCount = (int)((fileSize + PieceSize - 1) / PieceSize),
                PiecesRoot = new byte[32]
            });

            // BEP 52: the next file starts on a piece boundary.
            offset += (fileSize + PieceSize - 1) / PieceSize * PieceSize;
        }

        info.FullSize = offset;
        return info;
    }
}
