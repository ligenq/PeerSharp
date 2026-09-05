using PeerSharp.Internals;

namespace PeerSharp.Tests.Api;

public sealed class ConsumerSupportTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CreateMagnet_RoundTripsFullHashesAndEscapesQueryValues(bool v1, bool v2)
    {
        var hash = v1 ? InfoHash.CreateRandom() : InfoHash.Empty;
        var hashV2 = v2 ? InfoHash.CreateRandomV2() : InfoHash.EmptyV2;
        const string name = "a & b + å?#";
        string[] trackers = ["https://example.invalid/announce?key=a&b=2", "udp://example.invalid:6969/announce"];
        var magnet = MagnetLink.Create(hash, hashV2, name, trackers);
        var parsed = MagnetLink.Parse(magnet.ToString());

        Assert.Equal(hash, parsed.InfoHash);
        Assert.Equal(hashV2, parsed.InfoHashV2);
        Assert.Equal(name, parsed.DisplayName);
        Assert.Equal(trackers, parsed.Trackers);
        Assert.Equal(v1, magnet.ToString().Contains("urn:btih:", StringComparison.Ordinal));
        Assert.Equal(v2, magnet.ToString().Contains("urn:btmh:1220", StringComparison.Ordinal));
        trackers[0] = "changed";
        Assert.NotEqual(trackers[0], magnet.Trackers[0]);
    }

    [Fact]
    public void CreateMagnet_RejectsAbsentOrMisplacedHashes()
    {
        Assert.Throws<ArgumentException>(() => MagnetLink.Create());
        Assert.Throws<ArgumentException>(() => MagnetLink.Create(InfoHash.Empty, InfoHash.EmptyV2));
        Assert.Throws<ArgumentException>(() => MagnetLink.Create(InfoHash.CreateRandomV2()));
        Assert.Throws<ArgumentException>(() => MagnetLink.Create(infoHashV2: InfoHash.CreateRandom()));
    }

    [Fact]
    public void CreateMagnet_FiltersBlankAndDuplicateTrackersWithoutChangingUrlCase()
    {
        var link = MagnetLink.Create(InfoHash.CreateRandom(), trackers:
            ["", " ", "https://example.invalid/A", "https://example.invalid/A", "https://example.invalid/a"]);
        Assert.Equal(new[] { "https://example.invalid/A", "https://example.invalid/a" }, link.Trackers);
        Assert.Equal(link.Trackers, MagnetLink.Parse(link.ToString()).Trackers);
        Assert.Throws<ArgumentNullException>(() => MagnetLink.FromTorrent(null!));
        Assert.Throws<ArgumentNullException>(() => MagnetLink.FromTorrentFile(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateMagnet_FromTorrentWorksBeforeMetadataAndTrackersAreOptional(bool includeTrackers)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "metadata pending";
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata,
            trackerFactory: new PeerSharp.Internals.Trackers.TrackerFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        torrent.Trackers.AddTracker("https://example.invalid/announce");

        var link = MagnetLink.FromTorrent(torrent, includeTrackers);

        Assert.False(torrent.HasMetadata);
        Assert.Equal(torrent.HashV2, link.InfoHashV2);
        Assert.False(link.IsV1);
        Assert.Equal(torrent.Name, link.DisplayName);
        Assert.Equal(includeTrackers ? 1 : 0, link.Trackers.Count);
    }

    [Fact]
    public void CreateMagnet_FromFileIncludesBothHybridHashes()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        metadata.AnnounceList.Add("https://example.invalid/announce");
        var file = new TorrentFile(metadata);

        var link = MagnetLink.FromTorrentFile(file, includeTrackers: true);

        Assert.Equal(file.InfoHash, link.InfoHash);
        Assert.Equal(file.InfoHashV2, link.InfoHashV2);
        Assert.Single(link.Trackers);
        Assert.Empty(MagnetLink.FromTorrentFile(file).Trackers);
    }

    [Fact]
    public async Task TransferSnapshot_ObservesCurrentCountersWithoutAnAlertSubscriber()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal();
        ITorrent api = torrent;
        Assert.Null(api.Events);
        torrent.FileTransferInternal.Downloader.AddDownloaded(2048);
        torrent.FileTransferInternal.Uploader.AddUploaded(1024);
        Volatile.Write(ref torrent._lastReportedDownloadSpeed, 500);
        Volatile.Write(ref torrent._lastReportedUploadSpeed, 250);

        var first = api.GetTransferStats();
        torrent.FileTransferInternal.Downloader.AddDownloaded(100);
        var second = api.GetTransferStats();

        Assert.Equal(2048, first.Downloaded);
        Assert.Equal(2148, second.Downloaded);
        Assert.Equal(1024, first.Uploaded);
        Assert.Equal(500, first.DownloadSpeed);
        Assert.Equal(250, first.UploadSpeed);
        Assert.Equal(api.Peers.ConnectedCount, first.ConnectedPeers);
        Assert.Equal(0.5f, first.Ratio);
    }

    [Fact]
    public async Task EngineStats_LifetimeBytesSurviveRemovalAndRemainScopedToOneEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), "PeerSharpConsumer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { Files = { DefaultDownloadPath = path } };
            await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
            await using var other = ClientEngine.Create(new TorrentClientOptions { Settings = new Settings() });
            var file = new TorrentFileBuilder().AddFile("empty", []).Build();
            var torrent = (Torrent)await engine.AddTorrentAsync(file, new AddTorrentOptions(path) { StartImmediately = false });
            torrent.FileTransferInternal.Downloader.AddDownloaded(4096);
            torrent.FileTransferInternal.Uploader.AddUploaded(1024);

            var before = engine.GetStats();
            Assert.Equal(4096, before.TotalDownloaded);
            Assert.Equal(4096, before.LifetimeDownloaded);
            Assert.Equal(1024, before.LifetimeUploaded);
            await engine.RemoveTorrentAsync(torrent);
            var after = engine.GetStats();

            Assert.Equal(0, after.TotalDownloaded);
            Assert.Equal(0, after.TorrentCount);
            Assert.Equal(before.LifetimeDownloaded, after.LifetimeDownloaded);
            Assert.Equal(before.LifetimeUploaded, after.LifetimeUploaded);
            Assert.Equal(0, other.GetStats().LifetimeDownloaded);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Theory]
    [InlineData(ProxyType.None, "", true, true, true, true, true)]
    [InlineData(ProxyType.Socks5, "localhost", true, true, true, true, true)]
    [InlineData(ProxyType.Http, "", true, true, true, true, true)]
    [InlineData(ProxyType.Http, "localhost", true, true, false, false, false)]
    [InlineData(ProxyType.Http, "localhost", false, true, false, true, false)]
    [InlineData(ProxyType.Http, "localhost", true, false, false, false, true)]
    [InlineData(ProxyType.Http, "localhost", false, false, false, true, true)]
    public void UdpCapabilities_RespectTrafficRoutingWithoutChangingSettings(
        ProxyType type, string host, bool peers, bool trackers, bool dhtAllowed, bool utpAllowed, bool trackersAllowed)
    {
        var settings = new ProxySettings { Type = type, Host = host, ProxyPeers = peers, ProxyTrackers = trackers };

        var capabilities = settings.GetUdpCapabilities();

        Assert.Equal(dhtAllowed, capabilities.SupportsDht);
        Assert.Equal(utpAllowed, capabilities.SupportsUtp);
        Assert.Equal(trackersAllowed, capabilities.SupportsUdpTrackers);
        Assert.Equal(type, settings.Type);
        Assert.Equal(host, settings.Host);
        Assert.Equal(peers, settings.ProxyPeers);
        Assert.Equal(trackers, settings.ProxyTrackers);
    }
}
