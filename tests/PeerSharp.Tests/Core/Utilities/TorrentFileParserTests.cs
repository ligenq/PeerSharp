using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core.Utilities;

public class TorrentFileParserTests
{
    [Fact]
    public void Parse_SingleFileV1_ParsesCorrectly()
    {
        var info = new BDict();
        info.Dict["name"] = new BString("test.txt"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["length"] = new BNumber(1000);
        info.Dict["pieces"] = new BString(new byte[20]); // One piece hash

        var root = new BDict();
        root.Dict["announce"] = new BString("http://tracker/announce"u8.ToArray());
        root.Dict["info"] = info;

        var data = BencodeWriter.Write(root);
        var metadata = TorrentFileParser.Parse(data);

        Assert.Equal("http://tracker/announce", metadata.Announce);
        Assert.Equal("test.txt", metadata.Info.Name);
        Assert.Equal(1000, metadata.Info.FullSize);
        Assert.Equal(16384u, metadata.Info.PieceSize);
        Assert.Single(metadata.Info.Files);
        Assert.Equal("test.txt", metadata.Info.Files[0].Path);
        Assert.Equal(TorrentVersion.V1, metadata.Info.Version);
    }

    [Fact]
    public void Parse_MultiFileV1_ParsesCorrectly()
    {
        var f1 = new BDict();
        f1.Dict["length"] = new BNumber(500);
        var p1 = new BList(); p1.List.Add(new BString("dir"u8.ToArray())); p1.List.Add(new BString("f1.txt"u8.ToArray()));
        f1.Dict["path"] = p1;

        var f2 = new BDict();
        f2.Dict["length"] = new BNumber(500);
        var p2 = new BList(); p2.List.Add(new BString("f2.txt"u8.ToArray()));
        f2.Dict["path"] = p2;

        var files = new BList();
        files.List.Add(f1);
        files.List.Add(f2);

        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["files"] = files;
        info.Dict["pieces"] = new BString(new byte[20]);

        var root = new BDict();
        root.Dict["info"] = info;

        var data = BencodeWriter.Write(root);
        var metadata = TorrentFileParser.Parse(data);

        Assert.Equal(1000, metadata.Info.FullSize);
        Assert.Equal(2, metadata.Info.Files.Count);
        Assert.Equal("dir" + Path.DirectorySeparatorChar + "f1.txt", metadata.Info.Files[0].Path);
        Assert.Equal("f2.txt", metadata.Info.Files[1].Path);
    }

    [Fact]
    public void Parse_MultiFileV1_AttrPMarksPaddingEvenWithoutPadPath()
    {
        var f1 = new BDict();
        f1.Dict["length"] = new BNumber(100);
        var p1 = new BList(); p1.List.Add(new BString("file.bin"u8.ToArray()));
        f1.Dict["path"] = p1;

        var padding = new BDict();
        padding.Dict["length"] = new BNumber(156);
        padding.Dict["attr"] = new BString("p"u8.ToArray());
        var padPath = new BList(); padPath.List.Add(new BString("padding.bin"u8.ToArray()));
        padding.Dict["path"] = padPath;

        var files = new BList();
        files.List.Add(f1);
        files.List.Add(padding);

        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(256);
        info.Dict["files"] = files;
        info.Dict["pieces"] = new BString(new byte[20]);

        var root = new BDict();
        root.Dict["info"] = info;

        var metadata = TorrentFileParser.Parse(BencodeWriter.Write(root));

        Assert.False(metadata.Info.Files[0].IsPadding);
        Assert.True(metadata.Info.Files[1].IsPadding);
    }

    [Fact]
    public void Parse_V2FileTree_ParsesCorrectly()
    {
        // BEP 52 structure
        var fileInfo1 = new BDict();
        fileInfo1.Dict["length"] = new BNumber(60);
        fileInfo1.Dict["pieces root"] = new BString(new byte[32]);

        var fileNode1 = new BDict();
        fileNode1.Dict[""] = fileInfo1;

        var dirNode = new BDict();
        dirNode.Dict["a.bin"] = fileNode1;

        var fileInfo2 = new BDict();
        fileInfo2.Dict["length"] = new BNumber(80);
        fileInfo2.Dict["pieces root"] = new BString(new byte[32]);

        var fileNode2 = new BDict();
        fileNode2.Dict[""] = fileInfo2;

        var fileTree = new BDict();
        fileTree.Dict["dir"] = dirNode;
        fileTree.Dict["b.bin"] = fileNode2;

        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["meta version"] = new BNumber(2);
        info.Dict["file tree"] = fileTree;

        var root = new BDict();
        root.Dict["info"] = info;

        var data = BencodeWriter.Write(root);
        var metadata = TorrentFileParser.Parse(data);

        Assert.Equal(TorrentVersion.V2, metadata.Info.Version);
        Assert.Equal(2, metadata.Info.Files.Count);

        var fileA = metadata.Info.Files.Single(f => f.Path.EndsWith("dir" + Path.DirectorySeparatorChar + "a.bin"));
        var fileB = metadata.Info.Files.Single(f => f.Path.EndsWith("b.bin"));

        Assert.Equal(60, fileA.Size);
        Assert.Equal(80, fileB.Size);
        Assert.Equal((uint)16384, metadata.Info.PieceSize);

        var offsets = new[] { fileA.Offset, fileB.Offset };
        Assert.Contains(0, offsets);
        Assert.Contains(16384, offsets);
        Assert.Equal(16384 * 2, metadata.Info.FullSize);
    }

    [Fact]
    public void Parse_V2FileLargerThanPiece_RequiresValidPieceLayer()
    {
        byte[] fileData = new byte[32 * 1024];
        Random.Shared.NextBytes(fileData);
        var leaves = MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = MerkleTree.ComputeRoot(leaves);

        var fileInfo = new BDict();
        fileInfo.Dict["length"] = new BNumber(fileData.Length);
        fileInfo.Dict["pieces root"] = new BString(piecesRoot);

        var fileNode = new BDict();
        fileNode.Dict[""] = fileInfo;

        var fileTree = new BDict();
        fileTree.Dict["large.bin"] = fileNode;

        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16_384);
        info.Dict["meta version"] = new BNumber(2);
        info.Dict["file tree"] = fileTree;

        var root = new BDict();
        root.Dict["info"] = info;

        var ex = Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
        Assert.Contains("piece layers", ex.Message, StringComparison.OrdinalIgnoreCase);

        var pieceLayer = MerkleTree.GetPieceLayer(leaves, 16_384);
        byte[] layerData = pieceLayer.SelectMany(hash => hash).ToArray();
        var layers = new BDict();
        layers.Dict[System.Text.Encoding.Latin1.GetString(piecesRoot)] = new BString(layerData);
        root.Dict["piece layers"] = layers;

        var metadata = TorrentFileParser.Parse(BencodeWriter.Write(root));

        Assert.Equal(TorrentVersion.V2, metadata.Info.Version);
        Assert.NotNull(metadata.Info.Files[0].PieceLayers);
        Assert.Equal(2, metadata.Info.Files[0].PieceLayers!.Count);
    }

    [Fact]
    public void ParseInfoBytes_V2FileLargerThanPiece_AllowsMissingPieceLayer()
    {
        byte[] fileData = new byte[32 * 1024];
        Random.Shared.NextBytes(fileData);
        var piecesRoot = MerkleTree.ComputeRoot(MerkleTree.ComputeLeaves(fileData));

        var fileInfo = new BDict();
        fileInfo.Dict["length"] = new BNumber(fileData.Length);
        fileInfo.Dict["pieces root"] = new BString(piecesRoot);

        var fileNode = new BDict();
        fileNode.Dict[""] = fileInfo;

        var fileTree = new BDict();
        fileTree.Dict["large.bin"] = fileNode;

        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16_384);
        info.Dict["meta version"] = new BNumber(2);
        info.Dict["file tree"] = fileTree;

        var metadata = TorrentFileParser.ParseInfoBytes(BencodeWriter.Write(info));

        Assert.Equal(TorrentVersion.V2, metadata.Info.Version);
        Assert.Null(metadata.Info.Files[0].PieceLayers);
    }

    [Fact]
    public void Parse_V2UnsupportedMetaVersion_Throws()
    {
        var info = new BDict();
        info.Dict["name"] = new BString("root"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16_384);
        info.Dict["meta version"] = new BNumber(3);
        info.Dict["file tree"] = new BDict();

        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void Parse_MerkleBEP30_ParsesCorrectly()
    {
        var info = new BDict();
        info.Dict["name"] = new BString("merkle.txt"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["length"] = new BNumber(1000);
        info.Dict["root hash"] = new BString(new byte[20]);

        var root = new BDict();
        root.Dict["info"] = info;

        var data = BencodeWriter.Write(root);
        var metadata = TorrentFileParser.Parse(data);

        Assert.True(metadata.Info.IsMerkle);
        Assert.NotNull(metadata.Info.MerkleRootHash);
        Assert.Empty(metadata.Info.Pieces);
    }

    [Fact]
    public void Parse_V1NegativePieceLength_Throws()
    {
        var info = CreateSingleFileV1Info(pieceLength: -1, length: 1000, pieces: new byte[20]);
        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void Parse_V1NegativeFileLength_Throws()
    {
        var info = CreateSingleFileV1Info(pieceLength: 16384, length: -1, pieces: []);
        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void Parse_V1MissingPieces_Throws()
    {
        var info = new BDict();
        info.Dict["name"] = new BString("test.txt"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["length"] = new BNumber(1000);

        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void Parse_V1PiecesLengthNotMultipleOfHashSize_Throws()
    {
        var info = CreateSingleFileV1Info(pieceLength: 16384, length: 1000, pieces: new byte[21]);
        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void Parse_V1PiecesCountMismatch_Throws()
    {
        var info = CreateSingleFileV1Info(pieceLength: 16, length: 1000, pieces: new byte[20]);
        var root = new BDict();
        root.Dict["info"] = info;

        Assert.Throws<FormatException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    private static BDict CreateSingleFileV1Info(long pieceLength, long length, byte[] pieces)
    {
        var info = new BDict();
        info.Dict["name"] = new BString("test.txt"u8.ToArray());
        info.Dict["piece length"] = new BNumber(pieceLength);
        info.Dict["length"] = new BNumber(length);
        info.Dict["pieces"] = new BString(pieces);
        return info;
    }
}





