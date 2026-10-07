using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utp;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core;

public class ClientEngineTests
{
    [Fact]
    public async Task AddMagnetAsync_CopiesSelectionsForFutureMetadata()
    {
        await using var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        FileSelection[] selections = [new(true, Priority.High)];
        var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:" + new string('a', 40));
        var torrent = (Torrent)await engine.AddMagnetAsync(magnet,
            new AddTorrentOptions { StartImmediately = false, FileSelections = selections });
        selections[0] = new(false, Priority.DoNotDownload);
        Assert.Equal(new FileSelection(true, Priority.High), Assert.Single(torrent.PendingFileSelections!));
    }

    [Fact]
    public async Task AddTorrentAsync_InvalidOptionsDoNotRegisterTorrent()
    {
        await using var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.AddTorrentAsync(
            new TorrentFile(ThreeFileMetadata()), new AddTorrentOptions { StartImmediately = false, RatioLimit = float.NaN }));
        Assert.Empty(engine.GetTorrents());
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_DuringInitialization_WaitsForNetworkStartBeforeDisposal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _networkManager.StartHandler = async () => { entered.SetResult(); await release.Task; };
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        var initialize = engine.InitializeAsync();
        await entered.Task;
        var dispose = engine.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, _networkManager.DisposeCallCount);
        release.SetResult();
        await initialize;
        await dispose;
        Assert.Equal(1, _networkManager.DisposeCallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.InitializeAsync());
    }

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
        public int StopCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public Func<Task>? StartHandler { get; set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return StartHandler?.Invoke() ?? Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            Stopped = true;
            StopCallCount++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<PortMappingStatus> GetPortMappingStatus()
        {
            return new List<PortMappingStatus>();
        }

        public static void Dispose() { }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
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
        // A real torrent file always has one, and a torrent is now only findable by a hash it has.
        info.Info.Hash = InfoHash.CreateRandom();
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
    public async Task AddTorrentAsync_SeedsAdditionalPeerHints()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();
        var info = new TorrentFileMetadata();
        info.Info.Hash = InfoHash.CreateRandom();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 51413);

        var torrent = await engine.AddTorrentAsync(
            new TorrentFile(info),
            new AddTorrentOptions { StartImmediately = false, AdditionalPeers = [endpoint] });

        Assert.Equal(0, torrent.Peers.Add([endpoint]));
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_AppliesTheGivenFileSelection()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        var torrent = await engine.AddTorrentAsync(
            new TorrentFile(ThreeFileMetadata()),
            new AddTorrentOptions
            {
                StartImmediately = false,
                FileSelections = [new FileSelection(), new FileSelection(false, Priority.DoNotDownload), new FileSelection(true, Priority.High)],
            });

        var selections = torrent.GetAllFileSelections();
        Assert.True(selections[0].Selected);
        Assert.False(selections[1].Selected);
        Assert.Equal(Priority.High, selections[2].Priority);
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_RefusesAFileSelectionOfTheWrongLength()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddTorrentAsync(
            new TorrentFile(ThreeFileMetadata()),
            new AddTorrentOptions { StartImmediately = false, FileSelections = [new FileSelection()] }));

        Assert.Empty(engine.GetTorrents());
    }

    [Fact(Timeout = 30000)]
    public async Task AddTorrentAsync_AddsTheGivenTrackersAndWebSeeds()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        var torrent = await engine.AddTorrentAsync(
            new TorrentFile(ThreeFileMetadata()),
            new AddTorrentOptions
            {
                StartImmediately = false,
                AdditionalTrackers = ["http://tracker.example/announce"],
                AdditionalWebSeeds = ["http://seed.example/files/"],
            });

        Assert.Contains(torrent.Trackers.GetTrackers(), tracker => tracker.Url == "http://tracker.example/announce");
        Assert.Contains("http://seed.example/files/", torrent.WebSeeds.GetAll());
    }

    private static TorrentFileMetadata ThreeFileMetadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.Name = "three";
        metadata.Info.PieceSize = 100;
        metadata.Info.FullSize = 300;
        for (int i = 0; i < 3; i++)
        {
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = $"f{i}", Size = 100, Offset = i * 100 });
            metadata.Info.Pieces.Add(new byte[20]);
        }

        return metadata;
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
    public async Task RemoveTorrentAsync_ByTruncatedV2Hash_RemovesOnlyThatTorrent()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        static TorrentFile CreateV2File(string name)
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = name;
            metadata.Info.Version = TorrentVersion.V2;
            metadata.Info.HashV2 = InfoHash.CreateRandomV2();
            metadata.Info.PieceSize = 16_384;
            metadata.Info.FullSize = 16_384;
            metadata.Info.Files.Add(new Internals.TorrentFileEntry
            {
                Path = $"{name}.bin",
                Size = 16_384,
                Offset = 0,
                FirstPieceIndex = 0,
                PieceCount = 1,
                PiecesRoot = new byte[32]
            });
            return new TorrentFile(metadata);
        }

        var options = new AddTorrentOptions { StartImmediately = false };
        var first = await engine.AddTorrentAsync(CreateV2File("first"), options);
        var second = await engine.AddTorrentAsync(CreateV2File("second"), options);

        await engine.RemoveTorrentAsync(second.HashV2.TruncateToV1());

        Assert.Same(first, engine.GetTorrent(first.HashV2));
        Assert.Null(engine.GetTorrent(second.HashV2));
        Assert.Single(engine.GetTorrents());
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
    public async Task InitializeAsync_AlertsWhenAConfiguredPortCouldNotBeUsed()
    {
        ushort requestedTcp = _settings.Connection.TcpPort;
        ushort requestedUdp = _settings.Connection.UdpPort;
        _networkManager.BoundTcpPort = 12345;
        _networkManager.BoundUdpPort = 23456;
        var alerts = new AlertsManager(_timeProvider);
        alerts.RegisterAlerts((uint)AlertCategory.Session);
        var engine = ClientEngine.Create(
            _settings,
            alerts: alerts,
            networkManager: _networkManager,
            timeProvider: _timeProvider);

        await engine.InitializeAsync();

        var changed = alerts.PopAlerts().OfType<ListenPortChangedAlert>().ToArray();
        Assert.Collection(
            changed.OrderBy(alert => alert.Transport),
            tcp =>
            {
                Assert.Equal(ListenTransport.Tcp, tcp.Transport);
                Assert.Equal(requestedTcp, tcp.RequestedPort);
                Assert.Equal(12345, tcp.ActualPort);
            },
            udp =>
            {
                Assert.Equal(ListenTransport.Udp, udp.Transport);
                Assert.Equal(requestedUdp, udp.RequestedPort);
                Assert.Equal(23456, udp.ActualPort);
            });
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

    [Fact(Timeout = 30000)]
    public async Task DisposeAsync_DelegatesNetworkShutdownToNetworkDisposeOnly()
    {
        var engine = ClientEngine.Create(_settings, networkManager: _networkManager, timeProvider: _timeProvider);
        await engine.InitializeAsync();

        await engine.DisposeAsync();

        Assert.Equal(0, _networkManager.StopCallCount);
        Assert.Equal(1, _networkManager.DisposeCallCount);
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




