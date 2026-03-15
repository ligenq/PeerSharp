using System.Text;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests.Core.Utilities.Bencode;

public class BencodeEdgeTests
{
    [Fact]
    public void TestIntegerOverflow()
    {
        // 9223372036854775808 is long.MaxValue + 1
        var data = Encoding.ASCII.GetBytes("i9223372036854775808e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void TestLeadingZero()
    {
        var data = Encoding.ASCII.GetBytes("i03e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void TestNegativeZero()
    {
        var data = Encoding.ASCII.GetBytes("i-0e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void TestStringLengthOverflow()
    {
        // 2147483648 is int.MaxValue + 1
        var data = Encoding.ASCII.GetBytes("2147483648:a");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void TestStringLengthNegativeOverflow()
    {
         // length that overflows int to negative
        var data = Encoding.ASCII.GetBytes("4294967295:a");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }
}





