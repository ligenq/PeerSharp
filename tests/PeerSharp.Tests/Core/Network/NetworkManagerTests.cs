using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Network;

public class NetworkManagerTests
{
    private class MockDhtManager : IDhtManager
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken ct = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
        public void Announce(InfoHash hash, int port) { }
        public static void GetPeers(InfoHash hash) { }
        public void Ping(System.Net.IPEndPoint endpoint) { }
        public static void AddNode(System.Net.IPEndPoint endpoint) { }
        public void SetCallback(IDhtCallback callback) { }
        public void FindPeers(InfoHash hash) { }
        public void ScrapeInfoHash(InfoHash hash) { }
        public InfoHash NodeId => InfoHash.Empty;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public DhtState? ConsumeStateSnapshot() => null;
    }

    private class MockUtpManager : IUtpManager
    {
        public bool Started { get; private set; }
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void Start(IUdpListener listener)
        {
            Started = true;
        }

        public void Stop() { }
        public static void Receive(byte[] data, System.Net.IPEndPoint remote) { }
        public Task<UtpStream> ConnectAsync(System.Net.IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public UtpStream CreateStream(System.Net.IPEndPoint endpoint)
        {
            throw new NotImplementedException();
        }

        public void CloseStream(UtpStream stream) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, System.Net.IPEndPoint endpoint, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockPortListener : IPortListener
    {
        public bool Started { get; private set; }
        public int Port { get; private set; }
        public void Start(int port) { Started = true; Port = port; }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockUdpListener : IUdpListener
    {
        public bool Started { get; private set; }
        public int Port => 0;
        public void RegisterReceiver(IUdpReceiver receiver) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, System.Net.IPEndPoint endpoint, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) { Started = true; return Task.CompletedTask; }
        public void Stop() { }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockLsdManager : ILsdManager
    {
        public bool Started { get; private set; }
        public void Start()
        {
            Started = true;
        }

        public void Stop() { }
        public Task AnnounceAsync(InfoHash infoHash, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockPortMapperFactory : IPortMapperFactory
    {
        public IEnumerable<IPortMapper> CreateMappers(Settings settings)
        {
            return [];
        }
    }

    [Fact]
    public void PortMapperFactory_WithoutPortMappingSettings_ReturnsNoMappers()
    {
        var settings = new Settings();
        settings.Connection.UpnpPortMapping = false;
        settings.Connection.NatPmpPortMapping = false;

        var mappers = new PortMapperFactory().CreateMappers(settings);

        Assert.Empty(mappers);
    }

    [Fact]
    public void PortMapperFactory_WithEnabledSettings_ReturnsMatchingMappersInStableOrder()
    {
        var settings = new Settings();
        settings.Connection.UpnpPortMapping = true;
        settings.Connection.NatPmpPortMapping = true;

        var mappers = new PortMapperFactory().CreateMappers(settings).ToArray();

        Assert.Collection(
            mappers,
            mapper => Assert.IsType<UpnpPortMapping>(mapper),
            mapper => Assert.IsType<NatPmpPortMapping>(mapper));
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_StartsAllServices()
    {
        var settings = new Settings();
        settings.Dht.Enabled = true;
        settings.Connection.EnableLsd = true;
        settings.Connection.EnableTcpIn = true;
        settings.Connection.EnableUtpIn = true;
        settings.Connection.TcpPort = 5000;

        var dht = new MockDhtManager();
        var utp = new MockUtpManager();
        var port = new MockPortListener();
        var udp = new MockUdpListener();
        var lsd = new MockLsdManager();
        var mapper = new MockPortMapperFactory();

        var services = new NetworkServices(dht, utp, port, udp, lsd, mapper);
        var manager = new NetworkManager(settings, _ => { }, services);

        await manager.StartAsync();

        Assert.True(dht.Started);
        Assert.True(utp.Started);
        Assert.True(port.Started);
        Assert.Equal(5000, port.Port);
        Assert.True(udp.Started);
        Assert.True(lsd.Started);
    }

    [Fact]
    public async Task StartAsync_UdpFeaturesDisabled_DoesNotStartUdpListener()
    {
        var settings = new Settings();
        settings.Dht.Enabled = false;
        settings.Connection.EnableLsd = false;
        settings.Connection.EnableTcpIn = false;
        settings.Connection.EnableUtpIn = false;
        settings.Connection.EnableUtpOut = false;

        var dht = new MockDhtManager();
        var utp = new MockUtpManager();
        var port = new MockPortListener();
        var udp = new MockUdpListener();
        var lsd = new MockLsdManager();
        var mapper = new MockPortMapperFactory();

        var services = new NetworkServices(dht, utp, port, udp, lsd, mapper);
        var manager = new NetworkManager(settings, _ => { }, services);

        await manager.StartAsync();

        Assert.False(dht.Started);
        Assert.False(utp.Started);
        Assert.False(port.Started);
        Assert.False(udp.Started);
        Assert.False(lsd.Started);
    }
}





