namespace PeerSharp.Tests.Api;

public class TorrentFileTests
{
    [Fact]
    public void TryParse_InvalidData_ReturnsFalseWithError()
    {
        byte[] invalid = [0x01, 0x02, 0x03];

        bool ok = TorrentFile.TryParse(invalid, out var result, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parse_EmptySpan_Throws()
    {
        Assert.Throws<FormatException>(() => TorrentFile.Parse([]));
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

    [Fact]
    public void Load_FromPath_ParsesValidTorrent()
    {
        var data = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var original = new TorrentFileBuilder().AddFile("a.bin", data).Build();

        string tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempPath, original.RawData.ToArray());
            var loaded = TorrentFile.Load(tempPath);

            Assert.Equal(original.InfoHash, loaded.InfoHash);
            Assert.Equal(original.Name, loaded.Name);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}




