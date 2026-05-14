namespace PeerSharp.Tests.Core.Pieces;

public class PiecesProgressTests
{
    [Fact]
    public void Constructor_SetsCount()
    {
        var progress = new PiecesProgress(100);
        Assert.Equal(100, progress.Count);
        Assert.Equal(0, progress.ReceivedCount);
        Assert.False(progress.IsFull);
    }

    [Fact]
    public void SetHaveAll_MarksAllComplete()
    {
        var progress = new PiecesProgress(10);
        progress.SetHaveAll();
        Assert.Equal(10, progress.ReceivedCount);
        Assert.True(progress.IsFull);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(progress.HasPiece(i));
        }
    }

    [Fact]
    public void SetHaveNone_ClearsAll()
    {
        var progress = new PiecesProgress(10);
        progress.AddPiece(5);
        progress.SetHaveNone();
        Assert.Equal(0, progress.ReceivedCount);
        Assert.False(progress.IsFull);
        for (int i = 0; i < 10; i++)
        {
            Assert.False(progress.HasPiece(i));
        }
    }

    [Fact]
    public void AddPiece_UpdatesStats()
    {
        var progress = new PiecesProgress(10);
        progress.AddPiece(3);
        Assert.Equal(1, progress.ReceivedCount);
        Assert.True(progress.HasPiece(3));
        Assert.False(progress.HasPiece(0));

        progress.AddPiece(3); // Re-add
        Assert.Equal(1, progress.ReceivedCount);
    }

    [Fact]
    public void IsFull_TrueWhenAllPiecesAdded()
    {
        var progress = new PiecesProgress(3);
        progress.AddPiece(0);
        progress.AddPiece(1);
        Assert.False(progress.IsFull);
        progress.AddPiece(2);
        Assert.True(progress.IsFull);
    }

    [Fact]
    public void Bitfield_RoundTrip_Simple()
    {
        var progress = new PiecesProgress(10);
        progress.AddPiece(0);
        progress.AddPiece(7);
        progress.AddPiece(8);
        progress.AddPiece(9);

        var bitfield = progress.ToBitfield();
        // Bit 0, 7 are in byte 0. 8, 9 are in byte 1.
        // Byte 0: 10000001 = 0x81
        // Byte 1: 11000000 = 0xC0
        Assert.Equal(2, bitfield.Length);
        Assert.Equal(0x81, bitfield[0]);
        Assert.Equal(0xC0, bitfield[1]);

        var progress2 = new PiecesProgress(10);
        progress2.FromBitfield(bitfield);
        Assert.Equal(4, progress2.ReceivedCount);
        Assert.True(progress2.HasPiece(0));
        Assert.True(progress2.HasPiece(7));
        Assert.True(progress2.HasPiece(8));
        Assert.True(progress2.HasPiece(9));
    }

    [Fact]
    public void FromBitfield_FullBitfield_MarksAll()
    {
        var progress = new PiecesProgress(10);
        byte[] full = new byte[] { 0xFF, 0xC0 }; // 11111111 11000000
        progress.FromBitfield(full);
        Assert.True(progress.IsFull);
        Assert.Equal(10, progress.ReceivedCount);
    }

    [Fact]
    public void FromBitfield_PartialBytes()
    {
        var progress = new PiecesProgress(4);
        byte[] data = new byte[] { 0xA0 }; // 1010 0000
        progress.FromBitfield(data);
        Assert.True(progress.HasPiece(0));
        Assert.False(progress.HasPiece(1));
        Assert.True(progress.HasPiece(2));
        Assert.False(progress.HasPiece(3));
        Assert.Equal(2, progress.ReceivedCount);
    }

    [Fact]
    public void ToBitfield_FullState_ReturnsAllOnes()
    {
        var progress = new PiecesProgress(10);
        progress.SetHaveAll();
        var bitfield = progress.ToBitfield();
        Assert.Equal(0xFF, bitfield[0]);
        Assert.Equal(0xC0, bitfield[1]); // 11000000
    }

    [Fact]
    public void AddPiece_ThreadSafety()
    {
        const int count = 1000;
        var progress = new PiecesProgress(count);
        Parallel.For(0, count, progress.AddPiece);
        Assert.True(progress.IsFull);
        Assert.Equal(count, progress.ReceivedCount);
    }

    [Fact]
    public void AddPiece_OutOfRange_DoesNothing()
    {
        var progress = new PiecesProgress(5);
        progress.AddPiece(-1);
        progress.AddPiece(5);
        Assert.Equal(0, progress.ReceivedCount);
        Assert.False(progress.IsFull);
    }

    [Fact]
    public void FromBitfield_IgnoresSpareBitsInLastByte()
    {
        var progress = new PiecesProgress(10);
        byte[] data = new byte[] { 0xFF, 0xFF }; // extra bits set beyond 10 pieces
        progress.FromBitfield(data);
        Assert.True(progress.IsFull);
        Assert.Equal(10, progress.ReceivedCount);
    }
}






