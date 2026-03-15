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
}





