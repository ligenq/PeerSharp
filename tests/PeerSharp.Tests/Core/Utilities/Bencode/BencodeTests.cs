using System.Text;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests.Core.Utilities.Bencode;

public class BencodeTests
{
    [Fact]
    public void TestParseString()
    {
        var data = Encoding.ASCII.GetBytes("4:test");
        var node = BencodeParser.Parse(data);
        Assert.IsType<BString>(node);
        Assert.Equal("test", ((BString)node).Text);
    }

    [Fact]
    public void TestParseInt()
    {
        var data = Encoding.ASCII.GetBytes("i123e");
        var node = BencodeParser.Parse(data);
        Assert.IsType<BNumber>(node);
        Assert.Equal(123, ((BNumber)node).Value);
    }

    [Fact]
    public void TestParseList()
    {
        var data = Encoding.ASCII.GetBytes("l4:testi123ee");
        var node = BencodeParser.Parse(data);
        Assert.IsType<BList>(node);
        var list = (BList)node;
        Assert.Equal(2, list.List.Count);
        Assert.Equal("test", ((BString)list.List[0]).Text);
        Assert.Equal(123, ((BNumber)list.List[1]).Value);
    }

    [Fact]
    public void TestParseDict()
    {
        var data = Encoding.ASCII.GetBytes("d3:key5:valuee");
        var node = BencodeParser.Parse(data);
        Assert.IsType<BDict>(node);
        var dict = (BDict)node;
        Assert.Equal("value", dict.GetString("key"));
    }
}





