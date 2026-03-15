using PeerSharp.Internals;

namespace PeerSharp.Tests;

public sealed class ExceptionTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentTests", Guid.NewGuid().ToString("N"));
    private ITorrent _torrent = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "existing";
        metadata.Info.Hash = InfoHash.CreateRandom();
        _torrent = TorrentTestUtility.CreateMinimal(metadata, _tempDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TorrentException_CarriesInfoHashAndInnerException()
    {
        var hash = InfoHash.CreateRandom();
        var inner = new InvalidOperationException("inner");

        var ex = new TorrentException("failed", hash, inner);

        Assert.Equal("failed", ex.Message);
        Assert.Equal(hash, ex.InfoHash);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void TorrentNotFoundException_UsesProvidedHash()
    {
        var hash = InfoHash.CreateRandom();

        var ex = new TorrentNotFoundException(hash);

        Assert.Equal(hash, ex.InfoHash);
        Assert.Contains(hash.ToHexString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TorrentAlreadyExistsException_ExposesExistingTorrent()
    {
        var ex = new TorrentAlreadyExistsException(_torrent);

        Assert.Same(_torrent, ex.ExistingTorrent);
        Assert.Equal(_torrent.Hash, ex.InfoHash);
        Assert.Contains(_torrent.Name, ex.Message, StringComparison.Ordinal);
    }
}




