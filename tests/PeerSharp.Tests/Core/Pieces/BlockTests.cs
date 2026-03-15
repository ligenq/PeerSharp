namespace PeerSharp.Tests.Core.Pieces;

public class BlockTests
{
    [Fact]
    public void Constructor_RentsBuffer()
    {
        using var block = new Block(100, 0, 50);
        Assert.Equal(100, block.PieceIndex);
        Assert.Equal(0, block.Offset);
        Assert.Equal(50, block.Length);
        Assert.NotNull(block.Buffer);
        Assert.True(block.Buffer.Length >= 50);
    }

    [Fact]
    public void Data_ReturnsCorrectSlice()
    {
        using var block = new Block(100, 0, 10);
        Assert.Equal(10, block.Data.Length);
    }

    [Fact]
    public void Dispose_ReturnsBuffer()
    {
        var block = new Block(50);
        var buffer = block.Buffer;
        block.Dispose();
        Assert.Throws<ObjectDisposedException>(() => block.Buffer);
    }
}






