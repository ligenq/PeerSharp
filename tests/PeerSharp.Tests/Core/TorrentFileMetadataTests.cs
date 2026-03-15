namespace PeerSharp.Tests.Core;

public class TorrentFileMetadataTests
{
    [Fact]
    public void GetPieceRangeForFile_CorrectIndices()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 150 }); // Piece 0, 1
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 150, Size = 150 }); // Piece 1, 2
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var range0 = info.GetPieceRangeForFile(0);
        Assert.Equal(0, range0.firstPiece);
        Assert.Equal(1, range0.lastPiece);

        var range1 = info.GetPieceRangeForFile(1);
        Assert.Equal(1, range1.firstPiece);
        Assert.Equal(2, range1.lastPiece);
    }

    [Fact]
    public void GetFilesForPiece_CorrectFiles()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 150 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 150, Size = 150 });
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var files0 = info.GetFilesForPiece(0);
        Assert.Single(files0);
        Assert.Equal(0, files0[0]);

        var files1 = info.GetFilesForPiece(1);
        Assert.Equal(2, files1.Count);
        Assert.Equal(0, files1[0]);
        Assert.Equal(1, files1[1]);

        var files2 = info.GetFilesForPiece(2);
        Assert.Single(files2);
        Assert.Equal(1, files2[0]);
    }

    [Fact]
    public void GetPieceRangeForFile_UsesDerivedPieceCount_WhenPiecesMissing()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 60 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 120 });

        var range0 = info.GetPieceRangeForFile(0);
        Assert.Equal(0, range0.firstPiece);
        Assert.Equal(0, range0.lastPiece);

        var range1 = info.GetPieceRangeForFile(1);
        Assert.Equal(1, range1.firstPiece);
        Assert.Equal(2, range1.lastPiece);
    }

    [Fact]
    public void GetFilesForPiece_UsesDerivedPieceCount_WhenPiecesMissing()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 60 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 120 });

        var files0 = info.GetFilesForPiece(0);
        Assert.Single(files0);
        Assert.Equal(0, files0[0]);

        var files1 = info.GetFilesForPiece(1);
        Assert.Single(files1);
        Assert.Equal(1, files1[0]);

        var files2 = info.GetFilesForPiece(2);
        Assert.Single(files2);
        Assert.Equal(1, files2[0]);
    }

    [Fact]
    public void IsPieceNeeded_RespectsSelection()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 200;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 100 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 100 });
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var selection = new List<FileSelection>
        {
            new() { Selected = true },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };

        Assert.True(info.IsPieceNeeded(0, selection));
        Assert.False(info.IsPieceNeeded(1, selection));
    }

    [Fact]
    public void MerklePieceCount_CalculatesCorrectly()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 16384;
        info.FullSize = 16384 * 10 + 1; // 11 pieces

        Assert.Equal(11, info.MerklePieceCount);
    }
}





