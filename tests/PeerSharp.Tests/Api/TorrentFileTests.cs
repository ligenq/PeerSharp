namespace PeerSharp.Tests;

public class TorrentFileTests
{
    [Fact]
    public void TryParse_InvalidData_ReturnsFalseWithError()
    {
        byte[] invalid = { 0x01, 0x02, 0x03 };

        bool ok = TorrentFile.TryParse(invalid, out var result, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parse_EmptySpan_Throws()
    {
        Assert.Throws<FormatException>(() => TorrentFile.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void GetFile_OutOfRange_Throws()
    {
        var builder = new TorrentFileBuilder()
            .WithName("test")
            .WithPieceLength(16384)
            .AddFile("file.bin", new byte[1024]);

        var torrent = builder.Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.GetFile(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.GetFile(1));
    }

    [Fact]
    public void Equals_UsesInfoHash()
    {
        var data = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        var torrentA = new TorrentFileBuilder().AddFile("a.bin", data).Build();
        var torrentB = new TorrentFileBuilder().AddFile("a.bin", data).Build();

        Assert.Equal(torrentA.InfoHash, torrentB.InfoHash);
        Assert.True(torrentA.Equals(torrentB));
        Assert.True(torrentA == torrentB);
    }
}




