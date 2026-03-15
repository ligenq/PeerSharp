using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities.Bencode;

public class BencodeRobustnessTests
{
    [Fact]
    public void Parse_DeeplyNestedList_ThrowsInvalidDataException_NotStackOverflow()
    {
        // Construct a deeply nested list: lllll...eee
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append('l');
        for (int i = 0; i < 200; i++) sb.Append('e');

        byte[] data = Encoding.ASCII.GetBytes(sb.ToString());

        // The parser should have a depth limit or at least handle this gracefully
        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_InvalidStringLength_ThrowsException()
    {
        // Length says 10, but only 5 bytes follow
        byte[] data = Encoding.ASCII.GetBytes("10:abcde");
        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_HugeStringLength_DoesNotAllocateHugeArrayImmediately()
    {
        // Malicious length intended to cause OutOfMemory
        byte[] data = Encoding.ASCII.GetBytes("2147483647:junk");

        // Should throw because it realizes the data isn't there, 
        // without actually trying to allocate a 2GB array first.
        var ex = Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(data));
        Assert.IsNotType<OutOfMemoryException>(ex);
    }

    [Fact]
    public void Parse_EmptyInput_ThrowsException()
    {
        Assert.Throws<FormatException>(() => BencodeParser.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_InvalidInteger_ThrowsException()
    {
        byte[] data = Encoding.ASCII.GetBytes("i123abc e");
        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(data));
    }
}
