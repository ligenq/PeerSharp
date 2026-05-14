using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utp;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core;

public class ClientEngineTests
{
    private class MockNetworkManager : INetworkManager
    {
        public IDhtManager Dht { get; set; } = null!;
        public IUtpManager Utp { get; set; } = null!;
        public IPortListener PortListener { get; set; } = null!;
        public ILsdManager Lsd { get; set; } = null!;
        public IpBlocklist Blocklist { get; set; } = new();
        public int BoundTcpPort { get; set; }
        public int BoundUdpPort { get; set; }

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public IReadOnlyList<PortMappingStatus> GetPortMappingStatus()
        {
            return new List<PortMappingStatus>();
        }

        public static void Dispose() { }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockNetworkManager _networkManager = new();
    private readonly Settings _settings = new() { Files = { DefaultDownloadPath = "C:\\Downloads" } };

    [Fact(Timeout = 30000)]
    public async Task InitializeAsync_StartsNetworkManager()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);

        await engine.InitializeAsync();

        Assert.True(_networkManager.Started);
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_AddsToList()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);
        var torrentFile = new TorrentFile(info);

        var options = new AddTorrentOptions { StartImmediately = false };
        var torrent = await engine.AddTorrentAsync(torrentFile, options);

        Assert.NotNull(torrent);
        Assert.Equal("test", torrent.Name);
        Assert.Single(engine.GetTorrents());
        Assert.Equal(torrent, engine.GetTorrent(torrent.Hash));
    }

    [Fact(Timeout = 30000)]
    public async Task RemoveTorrentAsync_RemovesFromList()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        var info = new TorrentFileMetadata();
        info.Info.Hash = InfoHash.CreateRandom();
        var torrentFile = new TorrentFile(info);
        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await engine.RemoveTorrentAsync(torrent.Hash);

        Assert.Empty(engine.GetTorrents());
        Assert.Null(engine.GetTorrent(torrent.Hash));
    }

    [Fact(Timeout = 30000)]
    public async Task InitializeAsync_PreservesConfiguredPort_WhenNetworkManagerDoesNotBind()
    {
        // Network manager reports BoundTcpPort=0 (e.g. TCP disabled in WebTorrent-only setups).
        // The user-configured port must survive — trackers reject port=0 as "invalid port".
        _networkManager.BoundTcpPort = 0;
        _networkManager.BoundUdpPort = 0;
        ushort configuredTcp = _settings.Connection.TcpPort;
        ushort configuredUdp = _settings.Connection.UdpPort;

        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        Assert.Equal(configuredTcp, _settings.Connection.TcpPort);
        Assert.Equal(configuredUdp, _settings.Connection.UdpPort);
    }

    [Fact(Timeout = 30000)]
    public async Task InitializeAsync_OverwritesConfiguredPort_WhenNetworkManagerBindsDifferent()
    {
        _networkManager.BoundTcpPort = 12345;
        _networkManager.BoundUdpPort = 23456;

        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        Assert.Equal((ushort)12345, _settings.Connection.TcpPort);
        Assert.Equal((ushort)23456, _settings.Connection.UdpPort);
    }

    [Fact(Timeout = 30000)]
    public async Task GetStats_AggregatesFromTorrents()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        // Add a few torrents
        var info1 = new TorrentFileMetadata { Info = { Hash = InfoHash.CreateRandom(), Name = "t1" } };
        var info2 = new TorrentFileMetadata { Info = { Hash = InfoHash.CreateRandom(), Name = "t2" } };

        await engine.AddTorrentAsync(new TorrentFile(info1), new AddTorrentOptions { StartImmediately = false });
        await engine.AddTorrentAsync(new TorrentFile(info2), new AddTorrentOptions { StartImmediately = false });

        var stats = engine.GetStats();
        Assert.Equal(2, stats.TorrentCount);
    }

    [Fact(Timeout = 30000)]
    public async Task PublicMethods_ThrowObjectDisposedException_AfterDispose()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();
        var torrentFile = CreateTestTorrentFile();
        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });

        await engine.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => engine.ClearBlocklist());
        Assert.Throws<ObjectDisposedException>(() => engine.ClearGeoIp());
        Assert.Throws<ObjectDisposedException>(() => engine.GetPortMappingStatus());
        Assert.Throws<ObjectDisposedException>(() => engine.GetStats());
        Assert.Throws<ObjectDisposedException>(() => engine.GetTorrent(torrent.Hash));
        Assert.Throws<ObjectDisposedException>(() => engine.GetTorrents());
        Assert.Throws<ObjectDisposedException>(() => engine.LoadBlocklist(Stream.Null));
        Assert.Throws<ObjectDisposedException>(() => engine.LoadGeoIp(Stream.Null));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadBlocklistAsync(Stream.Null));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadGeoIpAsync(Stream.Null));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.SaveSessionAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StopAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.RemoveTorrentAsync(torrent.Hash));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.RemoveTorrentAsync(torrent));
    }

    private static TorrentFile CreateTestTorrentFile()
    {
        var info = new TorrentFileMetadata();
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);
        return new TorrentFile(info);
    }
}





