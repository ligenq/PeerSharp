using PeerSharp.Internals.Utilities;
using PeerSharp.Internals;
using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public class TorrentFileSerializerTests
{
    [Fact]
    public void BuildTorrentBytes_RequiresInfoBytes()
    {
        var metadata = new TorrentFileMetadata();
        var bytes = TorrentFileSerializer.BuildTorrentBytes(metadata);
        Assert.Null(bytes);
    }

    [Fact]
    public void BuildTorrentBytes_SerializesBasicFields()
    {
        var metadata = new TorrentFileMetadata();

        // Mock info dict
        var info = new BDict();
        info.Dict["name"] = new BString("test"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        metadata.InfoBytes = BencodeWriter.Write(info);

        metadata.Announce = "http://tracker.com/announce";
        metadata.AnnounceTiers.Add(["http://tracker1.com/announce", "http://tracker2.com/announce"]);
        metadata.WebSeedUrls.Add("http://seed.com/file");

        var bytes = TorrentFileSerializer.BuildTorrentBytes(metadata);
        Assert.NotNull(bytes);

        var root = BencodeParser.Parse(bytes!) as BDict;
        Assert.NotNull(root);

        Assert.Equal("http://tracker.com/announce", (root!.Dict["announce"] as BString)?.Text);

        var announceList = root.Dict["announce-list"] as BList;
        Assert.NotNull(announceList);
        Assert.Single(announceList!.List);
        var tier1 = announceList.List[0] as BList;
        Assert.Equal(2, tier1!.List.Count);

        Assert.Equal("http://seed.com/file", (root.Dict["url-list"] as BString)?.Text);

        var serializedInfo = root.Dict["info"] as BDict;
        Assert.NotNull(serializedInfo);
        Assert.Equal("test", (serializedInfo!.Dict["name"] as BString)?.Text);
    }

    [Fact]
    public void BuildTorrentBytes_WebSeedList_SerializesAsList()
    {
        var metadata = new TorrentFileMetadata();
        var info = new BDict();
        info.Dict["name"] = new BString("test"u8.ToArray());
        metadata.InfoBytes = BencodeWriter.Write(info);

        metadata.WebSeedUrls.Add("http://seed1.com");
        metadata.WebSeedUrls.Add("http://seed2.com");

        var bytes = TorrentFileSerializer.BuildTorrentBytes(metadata);
        var root = BencodeParser.Parse(bytes!) as BDict;

        var urlList = root!.Dict["url-list"] as BList;
        Assert.NotNull(urlList);
        Assert.Equal(2, urlList!.List.Count);
        Assert.Equal("http://seed1.com", (urlList.List[0] as BString)?.Text);
        Assert.Equal("http://seed2.com", (urlList.List[1] as BString)?.Text);
    }

    [Fact]
    public void BuildTorrentBytes_PieceLayers_SerializesCorrectly()
    {
        var metadata = new TorrentFileMetadata();
        var info = new BDict();
        info.Dict["name"] = new BString("test"u8.ToArray());
        metadata.InfoBytes = BencodeWriter.Write(info);

        byte[] hash = new byte[32]; // SHA256 for V2
        hash[0] = 0xAA;
        metadata.PieceLayers[hash] = [0xBB, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        var bytes = TorrentFileSerializer.BuildTorrentBytes(metadata);
        var root = BencodeParser.Parse(bytes!) as BDict;

        var pieceLayers = root!.Dict["piece layers"] as BDict;
        Assert.NotNull(pieceLayers);

        string key = Encoding.Latin1.GetString(hash);
        var layerData = pieceLayers!.Dict[key] as BString;
        Assert.NotNull(layerData);
        Assert.Equal(0xBB, layerData!.Value.Span[0]);
    }
}
