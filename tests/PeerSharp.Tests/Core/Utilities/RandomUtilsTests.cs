using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Utilities;

public sealed class RandomUtilsTests
{
    [Fact]
    public void Data_FillsByteArray()
    {
        var buffer = new byte[16];
        RandomUtils.Data(buffer);
        Assert.Equal(16, buffer.Length);
    }

    [Fact]
    public void Data_FillsSpan()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomUtils.Data(buffer);
        Assert.Equal(8, buffer.Length);
    }

    [Fact]
    public void Number_ReturnsUIntRange()
    {
        uint value = RandomUtils.Number();
        Assert.True(value <= uint.MaxValue);
    }

    [Fact]
    public void Number64_ReturnsUlongRange()
    {
        ulong value = RandomUtils.Number64();
        Assert.True(value <= ulong.MaxValue);
    }
}




