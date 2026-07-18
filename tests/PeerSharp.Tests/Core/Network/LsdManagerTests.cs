using System.Net;
using System.Net.Sockets;
using System.Text;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Network;

public class LsdManagerTests
{
    private class MockUdpSocket : IUdpSocket
    {
        public List<byte[]> SentPackets { get; } = [];
        public bool JoinedMulticast { get; private set; }
        private readonly TaskCompletionSource<UdpReceiveResult> _receiveTcs = new();

        public Socket Client { get; }

        public MockUdpSocket(AddressFamily family)
        {
            Client = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        }

        public void JoinMulticastGroup(IPAddress multicastAddr)
        {
            JoinedMulticast = true;
        }

        public void Close() { }
        public void Dispose()
        {
            Client.Dispose();
        }

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            return _receiveTcs.Task;
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
        {
            SentPackets.Add(datagram.ToArray());
            return new ValueTask<int>(datagram.Length);
        }
    }

    private class MockUdpSocketFactory : IUdpSocketFactory
    {
        public MockUdpSocket IPv4Socket { get; } = new(AddressFamily.InterNetwork);
        public MockUdpSocket IPv6Socket { get; } = new(AddressFamily.InterNetworkV6);

        public IUdpSocket Create(int port)
        {
            return IPv4Socket;
        }

        public IUdpSocket Create(AddressFamily family)
        {
            return family == AddressFamily.InterNetworkV6 ? IPv6Socket : IPv4Socket;
        }
    }

    private class MockResolver : ITorrentResolver
    {
        public Dictionary<InfoHash, Torrent> Torrents { get; } = [];
        public ITorrent? GetTorrent(InfoHash hash)
        {
            return Torrents.TryGetValue(hash, out var t) ? t : null;
        }

        public IReadOnlyList<ITorrent> GetTorrents()
        {
            return Torrents.Values.ToList<ITorrent>().AsReadOnly();
        }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockUdpSocketFactory _socketFactory = new();
    private readonly MockResolver _resolver = new();
    private readonly Settings _settings = new();

    public LsdManagerTests()
    {
        _settings.Connection.EnableLsd = true;
        _settings.Connection.TcpPort = 5000;
    }

    [Fact]
    public void Start_JoinsMulticastGroup()
    {
        var lsd = new LsdManager(_settings, _resolver, _timeProvider, _socketFactory);
        lsd.Start();

        Assert.True(_socketFactory.IPv4Socket.JoinedMulticast);
        Assert.True(_socketFactory.IPv6Socket.JoinedMulticast);
    }

    [Fact]
    public async Task Announce_SendsCorrectMessage()
    {
        var lsd = new LsdManager(_settings, _resolver, _timeProvider, _socketFactory);
        lsd.Start();

        var hash = InfoHash.CreateRandom();
        await lsd.AnnounceAsync(hash);

        // Verify IPv4 announcement
        Assert.Single(_socketFactory.IPv4Socket.SentPackets);
        var msgV4 = Encoding.ASCII.GetString(_socketFactory.IPv4Socket.SentPackets[0]);
        Assert.Contains("BT-SEARCH", msgV4);
        Assert.Contains("Host: 239.192.152.143:6771", msgV4);
        Assert.Contains("Port: 5000", msgV4);
        Assert.Contains($"Infohash: {hash.ToHexString()}", msgV4);

        // Verify IPv6 announcement
        Assert.Single(_socketFactory.IPv6Socket.SentPackets);
        var msgV6 = Encoding.ASCII.GetString(_socketFactory.IPv6Socket.SentPackets[0]);
        Assert.Contains("BT-SEARCH", msgV6);
        Assert.Contains("Host: [ff15::efc0:988f]:6771", msgV6);
        Assert.Contains("Port: 5000", msgV6);
        Assert.Contains($"Infohash: {hash.ToHexString()}", msgV6);
    }

    [Fact]
    public void ProcessMessage_AddsPeersToTorrent()
    {
        var lsd = new LsdManager(_settings, _resolver, _timeProvider, _socketFactory);
        var hash = InfoHash.CreateRandom();
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata { Info = { Hash = hash } });
        _resolver.Torrents[hash] = torrent;

        string message =
            "BT-SEARCH * HTTP/1.1\r\n" +
            "Port: 6000\r\n" +
            $"Infohash: {hash.ToHexString()}\r\n" +
            "cookie: other\r\n\r\n";

        var sender = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 12345);
        lsd.ProcessMessage(message, sender);

        // The announced peer (sender address + advertised port) must land in the
        // torrent's known-peers cache
        Assert.True(KnownPeersContain(torrent, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6000)));
    }

    [Fact]
    public void ProcessMessage_UnknownInfoHash_AddsNoPeers()
    {
        var lsd = new LsdManager(_settings, _resolver, _timeProvider, _socketFactory);
        var hash = InfoHash.CreateRandom();
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata { Info = { Hash = hash } });
        _resolver.Torrents[hash] = torrent;

        string message =
            "BT-SEARCH * HTTP/1.1\r\n" +
            "Port: 6000\r\n" +
            $"Infohash: {InfoHash.CreateRandom().ToHexString()}\r\n" +
            "cookie: other\r\n\r\n";

        lsd.ProcessMessage(message, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 12345));

        Assert.False(KnownPeersContain(torrent, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6000)));
    }

    [Fact]
    public void ProcessMessage_InvalidPort_AddsNoPeers()
    {
        var lsd = new LsdManager(_settings, _resolver, _timeProvider, _socketFactory);
        var hash = InfoHash.CreateRandom();
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata { Info = { Hash = hash } });
        _resolver.Torrents[hash] = torrent;

        string message =
            "BT-SEARCH * HTTP/1.1\r\n" +
            "Port: 0\r\n" +
            $"Infohash: {hash.ToHexString()}\r\n" +
            "cookie: other\r\n\r\n";

        lsd.ProcessMessage(message, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 12345));

        Assert.False(KnownPeersContain(torrent, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 0)));
    }

    private static bool KnownPeersContain(Torrent torrent, IPEndPoint endpoint)
    {
        var field = typeof(PeerSharp.Internals.Peers.PeerManager).GetField(
            "_knownPeersCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cache = (System.Collections.IDictionary)field.GetValue(torrent.PeersInternal)!;
        return cache.Contains(endpoint);
    }
}





