using PeerSharp.Internals.Utilities;
using System.Text;

namespace PeerSharp.Tests.Core.Utilities;

public class UrlEncodingTests
{
    [Fact]
    public void Encode_EmptySpan_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, UrlEncoding.Encode([]));
    }

    [Fact]
    public void Encode_UnreservedChars_ReturnsAsIs()
    {
        byte[] data = Encoding.ASCII.GetBytes("abcABC123.-_~");
        string result = UrlEncoding.Encode(data);
        Assert.Equal("abcABC123.-_~", result);
    }

    [Fact]
    public void Encode_SpecialChars_PercentEncodes()
    {
        // Space (0x20) -> %20
        // Null (0x00) -> %00
        // 0xFF -> %FF
        byte[] data = [0x20, 0x00, 0xFF];
        string result = UrlEncoding.Encode(data);
        Assert.Equal("%20%00%FF", result);
    }

    [Fact]
    public void Encode_LongBuffer_Works()
    {
        byte[] data = new byte[1000];
        Random.Shared.NextBytes(data);
        string result = UrlEncoding.Encode(data);
        Assert.NotEmpty(result);
        Assert.Contains("%", result);
    }
}





