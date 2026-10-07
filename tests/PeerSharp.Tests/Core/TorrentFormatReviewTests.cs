using System.Security.Cryptography;
using System.Text;
using PeerSharp.BEncoding;
using PeerSharp.Exceptions;
using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;
using Builder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core;

public sealed class TorrentFormatReviewTests
{
    [Theory]
    [InlineData(TorrentFileVersion.V2)]
    [InlineData(TorrentFileVersion.Hybrid)]
    public async Task UnicodePathsRoundTripAndBothHybridHashesVerify(TorrentFileVersion version)
    {
        var data = new Dictionary<string, byte[]>
        {
            ["z.bin"] = [3, 4, 5],
            ["räksmörgås/日本語.bin"] = [1, 2],
            ["a.bin"] = [7, 8, 9]
        };
        var builder = new Builder().WithVersion(version).WithPieceLength(16384);
        foreach (var (path, bytes) in data) builder.AddFile(path, bytes);
        var synchronous = builder.Build();
        var asynchronous = await builder.BuildAsync(TestContext.Current.CancellationToken);
        Assert.Equal(synchronous.InfoHashV2, asynchronous.InfoHashV2);
        var metadata = asynchronous.Metadata;
        foreach (var file in metadata.Info.Files)
        {
            var bytes = data[file.Path.Replace('\\', '/')];
            Assert.Equal(SHA256.HashData(bytes), file.PiecesRoot);
            if (version == TorrentFileVersion.Hybrid) Assert.True(metadata.Info.VerifyV1PieceHash(file.FirstPieceIndex, bytes));
        }
        var serialized = TorrentFileSerializer.BuildTorrentBytes(metadata)!;
        var reparsed = TorrentFileParser.Parse(serialized);
        Assert.Equal(metadata.Info.HashV2, reparsed.Info.HashV2);
        Assert.Equal(metadata.Info.Hash, reparsed.Info.Hash);
        Assert.Equal(metadata.Info.Files.Select(file => file.Path), reparsed.Info.Files.Select(file => file.Path));
        var tree = (BDict)((BDict)((BDict)BencodeParser.Parse(serialized)).Get("info")!).Get("file tree")!;
        Assert.Contains(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("räksmörgås")), tree.Dict.Keys);
    }

    [Fact]
    public void HybridSingleFileWithAnAdvisoryNameUsesAConsistentLayout()
    {
        var torrent = new Builder().WithVersion(TorrentFileVersion.Hybrid).WithName("display-name")
            .AddFile("dir/file.bin", [1, 2, 3]).Build();
        Assert.Equal("dir" + Path.DirectorySeparatorChar + "file.bin", Assert.Single(torrent.Metadata.Info.Files).Path);
        Assert.True(torrent.Metadata.Info.VerifyV1PieceHash(0, [1, 2, 3]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuilderRejectsDuplicateAndFileDirectoryConflicts(bool directoryConflict)
    {
        var builder = new Builder().WithVersion(TorrentFileVersion.V2).AddFile("a", [1]);
        builder.AddFile(directoryConflict ? "a/b" : "a", [2]);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("path")]
    [InlineData("padding")]
    public void HybridRejectsV1LayoutsThatDifferFromV2(string corruption)
    {
        var torrent = new Builder().WithVersion(TorrentFileVersion.Hybrid).WithPieceLength(16384)
            .AddFile("a", [1, 2]).AddFile("b", [3, 4]).Build();
        var root = (BDict)BencodeParser.Parse(torrent.RawData.ToArray());
        var info = (BDict)root.Get("info")!;
        var files = (BList)info.Get("files")!;
        if (corruption == "length") ((BDict)files.List[0]).Dict["length"] = new BNumber(3);
        else if (corruption == "path") ((BList)((BDict)files.List[0]).Get("path")!).List[0] = new BString("other"u8.ToArray());
        else files.List.RemoveAt(1);
        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }

    [Fact]
    public void FileTreeRequiresMetaVersionAndCannotDescribeAFileWithChildren()
    {
        var torrent = new Builder().WithVersion(TorrentFileVersion.V2).AddFile("a", [1]).Build();
        var root = (BDict)BencodeParser.Parse(torrent.RawData.ToArray());
        var info = (BDict)root.Get("info")!;
        info.Dict.Remove("meta version");
        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
        info.Dict["meta version"] = new BNumber(2);
        var tree = (BDict)info.Get("file tree")!;
        ((BDict)tree.Get("a")!).Dict["child"] = new BDict();
        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.Parse(BencodeWriter.Write(root)));
    }
}
