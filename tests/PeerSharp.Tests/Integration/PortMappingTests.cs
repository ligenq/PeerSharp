using System.Net;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public class PortMappingTests
{
    private readonly MockPortMapperFactory _mapperFactory;
    private readonly MockPortMapper _upnpMapper;
    private readonly MockPortMapper _natPmpMapper;

    public PortMappingTests()
    {
        _upnpMapper = new MockPortMapper("UPnP");
        _natPmpMapper = new MockPortMapper("NAT-PMP");
        _mapperFactory = new MockPortMapperFactory(_upnpMapper, _natPmpMapper);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_StartsPortMapping_WhenEnabled()
    {
        // Arrange
        var settings = new Settings();
        settings.Connection.UpnpPortMapping = true;
        settings.Connection.NatPmpPortMapping = true;
        settings.Connection.TcpPort = 6881;
        settings.Connection.UdpPort = 6881;

        var services = CreateMockServices(_mapperFactory);
        var manager = new NetworkManager(
            settings,
            _ => { },
            services
        );

        // Act

        // Act
        await manager.StartAsync();

        // Allow background task to run
        await Task.Delay(100);

        // Assert
        Assert.True(_upnpMapper.StartCalled);
        Assert.True(_natPmpMapper.StartCalled);
        Assert.True(_upnpMapper.MapCalled);
        Assert.True(_natPmpMapper.MapCalled);
        Assert.Equal(6881, _upnpMapper.MappedPort);
        Assert.Equal(6881, _natPmpMapper.MappedPort);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_UnmapsPorts_OnStop()
    {
        // Arrange
        var settings = new Settings();
        settings.Connection.UpnpPortMapping = true;
        var services = CreateMockServices(_mapperFactory);
        var manager = new NetworkManager(
            settings,
            _ => { },
            services
        );

        // Act

        await manager.StartAsync();
        await Task.Delay(50); // Let mapping start

        // Act
        await manager.StopAsync();

        // Allow background task to run
        await Task.Delay(100);

        // Assert
        Assert.True(_upnpMapper.UnmapCalled);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_StopAndDispose_UnmapsPortsOnlyOnce()
    {
        var settings = new Settings { Connection = { UpnpPortMapping = true } };
        var manager = new NetworkManager(settings, _ => { }, CreateMockServices(_mapperFactory));
        await manager.StartAsync();
        await Task.Delay(50);

        await manager.StopAsync();
        await manager.DisposeAsync();

        Assert.Equal(1, _upnpMapper.UnmapCallCount);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_CancelledStopCanBeRetried()
    {
        _upnpMapper.BlockUnmapUntilCancelled = true;
        var settings = new Settings { Connection = { UpnpPortMapping = true } };
        var manager = new NetworkManager(settings, _ => { }, CreateMockServices(_mapperFactory));
        await manager.StartAsync();
        await Task.Delay(50);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StopAsync(cancellation.Token));
        _upnpMapper.BlockUnmapUntilCancelled = false;
        await manager.StopAsync();

        Assert.Equal(2, _upnpMapper.UnmapCallCount);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_UnresponsivePortMapperDoesNotDelayShutdown()
    {
        _upnpMapper.BlockUnmapUntilCancelled = true;
        var settings = new Settings { Connection = { UpnpPortMapping = true } };
        var manager = new NetworkManager(settings, _ => { }, CreateMockServices(_mapperFactory));
        await manager.StartAsync();
        await Task.Delay(50);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await manager.StopAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {stopwatch.Elapsed}.");
        Assert.Equal(1, _upnpMapper.UnmapCallCount);
    }

    [Fact(Timeout = 30000)]
    public async Task NetworkManager_HandlesMappingFailure_Gracefully()
    {
        // Arrange
        _upnpMapper.ShouldThrow = true;
        var settings = new Settings { Connection = { UpnpPortMapping = true } };

        var services = CreateMockServices(_mapperFactory);
        var manager = new NetworkManager(
            settings,
            _ => { },
            services
        );

        // Act

        // Act & Assert
        // Should not throw
        await manager.StartAsync();
        await Task.Delay(100);
    }

    private static NetworkServices CreateMockServices(IPortMapperFactory mapperFactory)
    {
        return new NetworkServices(
            new MockDhtManager(),
            new MockUtpManager(),
            new MockPortListener(),
            new MockUdpListener(),
            new MockLsdManager(),
            mapperFactory
        );
    }

    // Mocks
    private class MockPortMapperFactory : IPortMapperFactory
    {
        private readonly MockPortMapper[] _mappers;

        public MockPortMapperFactory(params MockPortMapper[] mappers)
        {
            _mappers = mappers;
        }

        public IEnumerable<IPortMapper> CreateMappers(Settings settings)
        {
            // Simple logic for test: return relevant mock if setting enabled
            var result = new List<IPortMapper>();
            if (settings.Connection.UpnpPortMapping)
            {
                result.Add(_mappers.First(m => m.Name == "UPnP"));
            }

            if (settings.Connection.NatPmpPortMapping)
            {
                result.Add(_mappers.First(m => m.Name == "NAT-PMP"));
            }

            return result;
        }
    }

    private class MockPortMapper : IPortMapper
    {
        public string Name { get; }
        public bool StartCalled { get; private set; }
        public bool MapCalled { get; private set; }
        public bool UnmapCalled { get; private set; }
        public int UnmapCallCount { get; private set; }
        public int MappedPort { get; private set; }
        public bool ShouldThrow { get; set; }
        public bool BlockUnmapUntilCancelled { get; set; }

        public MockPortMapper(string name) => Name = name;

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalled = true;
            if (ShouldThrow)
            {
                throw new Exception("Simulated Failure");
            }

            return Task.CompletedTask;
        }

        public Task<bool> MapPortAsync(int port, string protocol, string description, CancellationToken ct = default)
        {
            MapCalled = true;
            MappedPort = port;
            if (ShouldThrow)
            {
                throw new Exception("Simulated Failure");
            }

            return Task.FromResult(true);
        }

        public async Task UnmapAllAsync(CancellationToken ct = default)
        {
            UnmapCalled = true;
            UnmapCallCount++;
            if (BlockUnmapUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }

        public IReadOnlyList<PortMappingStatus> GetStatus()
        {
            return new List<PortMappingStatus>();
        }
    }

    private class MockDhtManager : IDhtManager
    {
        public InfoHash NodeId { get; } = InfoHash.CreateRandom();
        public Task StartAsync(CancellationToken ct = default) { return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void SetCallback(IDhtCallback callback) { }
        public void Ping(IPEndPoint ep) { }
        public void Announce(InfoHash infoHash, int port) { }
        public void FindPeers(InfoHash infoHash) { }
        public void ScrapeInfoHash(InfoHash infoHash) { }
        public DhtState? ConsumeStateSnapshot() => null;
    }

    private class MockUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void Start(IUdpListener listener) { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public UtpStream CreateStream(IPEndPoint remote)
        {
            throw new NotImplementedException();
        }

        public void CloseStream(UtpStream stream) { }
        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private class MockPortListener : IPortListener
    {
        public int Port { get; private set; }
        public void Start(int port) { Port = port; }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockUdpListener : IUdpListener
    {
        public int Port { get; private set; }
        public void RegisterReceiver(IUdpReceiver receiver) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken token) { Port = 6881; return Task.CompletedTask; }
        public void Stop() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockLsdManager : ILsdManager
    {
        public void Start() { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task AnnounceAsync(InfoHash infoHash, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }
}
