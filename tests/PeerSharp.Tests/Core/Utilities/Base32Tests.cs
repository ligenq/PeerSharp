using PeerSharp.Internals.Utilities;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public class Base32Tests
{
    [Fact]
    public void Decode_EmptyString_ReturnsEmptyArray()
    {
        Assert.Empty(Base32.Decode(""));
        Assert.Empty(Base32.Decode(null!));
    }

    [Fact]
    public void Decode_ValidString_ReturnsCorrectBytes()
    {
        // "JBSWY3DPEBLW64TMMQQQ" -> "Hello World!"
        byte[] expected = Encoding.ASCII.GetBytes("Hello World!");
        byte[] actual = Base32.Decode("JBSWY3DPEBLW64TMMQQQ");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decode_WithPadding_ReturnsCorrectBytes()
    {
        byte[] expected = Encoding.ASCII.GetBytes("Hello");
        byte[] actual = Base32.Decode("JBSWY3DP====");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decode_Lowercase_ReturnsCorrectBytes()
    {
        byte[] expected = Encoding.ASCII.GetBytes("Hello World!");
        byte[] actual = Base32.Decode("jbswy3dpeblw64tmmqqq");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Decode_InvalidChar_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => Base32.Decode("JBSWY3DP!"));
    }

    [Fact]
    public void Decode_LongString_DoesNotOverflow()
    {
        // 20 characters (100 bits -> 12.5 bytes)
        string input = "ABCDEFGHIJKLMNOPQRST";
        // Just verify it doesn't throw and returns some data
        var result = Base32.Decode(input);
        Assert.Equal(12, result.Length);
    }
}





