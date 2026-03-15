using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Tests.Core;

public class TorrentConfigurationTests
{
    private readonly Torrent _torrent;
    private readonly MockBandwidthManager _bandwidthManager;
    private readonly TorrentConfiguration _config;

    public TorrentConfigurationTests()
    {
        _bandwidthManager = new MockBandwidthManager();
        // We need a dummy torrent, but we can't create TorrentConfiguration directly in isolation easily 
        // without a torrent because it requires an ITorrent back-reference.
        // However, we can use the one created by Torrent.Create.
        _torrent = TorrentTestUtility.CreateMinimal();
        // We can access the config through the torrent or create a new one with the mock bandwidth manager
        // But Torrent.Create uses a mock bandwidth manager from utility.
        // Let's reflection-swap or just use the one on the torrent if we can inject our mock.

        // Better: Instantiate TorrentConfiguration directly with a mock ITorrent
        _config = new TorrentConfiguration(_torrent, _bandwidthManager);
    }

    [Fact]
    public void DownloadLimit_Set_PropagatesToBandwidthManager()
    {
        _config.DownloadLimitBytesPerSecond = 5000;

        Assert.Equal(5000, _bandwidthManager.LastDownloadLimit);
        Assert.Equal(_torrent, _bandwidthManager.LastTorrent);
    }

    [Fact]
    public void UploadLimit_Set_PropagatesToBandwidthManager()
    {
        _config.UploadLimitBytesPerSecond = 2000;

        Assert.Equal(2000, _bandwidthManager.LastUploadLimit);
        Assert.Equal(_torrent, _bandwidthManager.LastTorrent);
    }

    [Fact]
    public void DownloadLimit_SetNegative_ClampsToZero()
    {
        _config.DownloadLimitBytesPerSecond = -100;
        Assert.Equal(0, _config.DownloadLimitBytesPerSecond);
        Assert.Equal(0, _bandwidthManager.LastDownloadLimit);
    }

    [Fact]
    public void UploadLimit_SetNegative_ClampsToZero()
    {
        _config.UploadLimitBytesPerSecond = -50;
        Assert.Equal(0, _config.UploadLimitBytesPerSecond);
        Assert.Equal(0, _bandwidthManager.LastUploadLimit);
    }

    [Fact]
    public void DiskReadLimit_SetsBandwidthManager()
    {
        _config.DiskReadLimitBytesPerSecond = 1234;
        Assert.Equal(1234, _config.DiskReadLimitBytesPerSecond);
        Assert.Equal(1234, _bandwidthManager.LastDiskReadLimit);
    }

    [Fact]
    public void DiskWriteLimit_SetsBandwidthManager()
    {
        _config.DiskWriteLimitBytesPerSecond = 4321;
        Assert.Equal(4321, _config.DiskWriteLimitBytesPerSecond);
        Assert.Equal(4321, _bandwidthManager.LastDiskWriteLimit);
    }

    private class MockBandwidthManager : IBandwidthManager
    {
        private readonly Dictionary<string, BandwidthChannel> _channels = new();
        public int LastDownloadLimit { get; private set; }
        public int LastDiskReadLimit { get; private set; }
        public int LastDiskWriteLimit { get; private set; }
        public int LastUploadLimit { get; private set; }
        public ITorrent? LastTorrent { get; private set; }

        public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit)
        {
            LastTorrent = torrent;
            LastDownloadLimit = downloadLimit;
            LastUploadLimit = uploadLimit;
        }

        public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit)
        {
            LastTorrent = torrent;
            LastDiskReadLimit = readLimit;
            LastDiskWriteLimit = writeLimit;
        }

        // Unused members
        public void Configure(int updateIntervalMs) { }
        public BandwidthChannel GetChannel(string name)
        {
            if (_channels.TryGetValue(name, out var channel))
            {
                return channel;
            }

            channel = new BandwidthChannel(TimeProvider.System);
            _channels[name] = channel;
            return channel;
        }
        public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent) => (0, 0);
        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent) => (0, 0);
        public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default) => Task.FromResult(0);
        public void ReturnBandwidth(int amount, string[] channelNames) { }
        public void RemoveTorrentChannels(ITorrent torrent) { }
        public void SetGlobalLimits(int downloadLimit, int uploadLimit) { }
        public void SetGlobalDiskLimits(int readLimit, int writeLimit) { }
        public void Start() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}




