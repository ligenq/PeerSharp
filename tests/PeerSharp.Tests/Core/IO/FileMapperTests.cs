using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class FileMapperTests
{
    [Fact]
    public void MapOffset_SingleFile_ReturnsCorrectOffsets()
    {
        var mapper = new FileMapper(new[] { 1000L });

        var (idx, offset) = mapper.MapOffset(0);
        Assert.Equal(0, idx);
        Assert.Equal(0, offset);

        (idx, offset) = mapper.MapOffset(500);
        Assert.Equal(0, idx);
        Assert.Equal(500, offset);

        (idx, offset) = mapper.MapOffset(1000);
        Assert.Equal(0, idx);
        Assert.Equal(1000, offset);
    }

    [Fact]
    public void MapOffset_MultipleFiles_ReturnsCorrectOffsets()
    {
        // File 0: 0-99
        // File 1: 100-299
        // File 2: 300-399
        var mapper = new FileMapper(new[] { 100L, 200L, 100L });

        var (idx, offset) = mapper.MapOffset(50);
        Assert.Equal(0, idx);
        Assert.Equal(50, offset);

        (idx, offset) = mapper.MapOffset(100);
        Assert.Equal(1, idx);
        Assert.Equal(0, offset);

        (idx, offset) = mapper.MapOffset(250);
        Assert.Equal(1, idx);
        Assert.Equal(150, offset);

        (idx, offset) = mapper.MapOffset(300);
        Assert.Equal(2, idx);
        Assert.Equal(0, offset);

        (idx, offset) = mapper.MapOffset(399);
        Assert.Equal(2, idx);
        Assert.Equal(99, offset);
    }

    [Fact]
    public void MapRange_SingleFile_ReturnsOneChunk()
    {
        var mapper = new FileMapper(new[] { 1000L });
        var ops = mapper.MapRange(100, 500).ToList();

        Assert.Single(ops);
        Assert.Equal(0, ops[0].FileIndex);
        Assert.Equal(100, ops[0].FileOffset);
        Assert.Equal(500, ops[0].Length);
        Assert.Equal(0, ops[0].BufferOffset);
    }

    [Fact]
    public void MapRange_SpanningFiles_ReturnsMultipleChunks()
    {
        // File 0: 100, File 1: 200, File 2: 100
        var mapper = new FileMapper(new[] { 100L, 200L, 100L });

        // Range 50 to 350 (length 300)
        // File 0: 50-99 (length 50)
        // File 1: 100-299 (length 200)
        // File 2: 300-349 (length 50)
        var ops = mapper.MapRange(50, 300).ToList();

        Assert.Equal(3, ops.Count);

        Assert.Equal(0, ops[0].FileIndex);
        Assert.Equal(50, ops[0].FileOffset);
        Assert.Equal(50, ops[0].Length);
        Assert.Equal(0, ops[0].BufferOffset);

        Assert.Equal(1, ops[1].FileIndex);
        Assert.Equal(0, ops[1].FileOffset);
        Assert.Equal(200, ops[1].Length);
        Assert.Equal(50, ops[1].BufferOffset);

        Assert.Equal(2, ops[2].FileIndex);
        Assert.Equal(0, ops[2].FileOffset);
        Assert.Equal(50, ops[2].Length);
        Assert.Equal(250, ops[2].BufferOffset);
    }
}





