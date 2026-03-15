using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Integration;

public class PeerManagerIntegrationTests
{
    [Fact(Timeout = 30000)]
    public async Task AddIncomingPeerAsync_AddsPeer()
    {
        string path = CreateTempPath();
        var metadata = CreateMetadata();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        torrent.Settings.Connection.Encryption = Encryption.Refuse;

        var timeProvider = new FakeTimeProvider();
        var manager = new PeerManager(torrent, new FakeGeoIpService(), new RealPeerFactory(), timeProvider, new FakeConnectionGovernor());

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var serverClient = await listener.AcceptTcpClientAsync();

        byte[] handshake = BuildHandshake(metadata.Info.Hash.Span, torrent.Settings.PeerId);
        await manager.AddIncomingPeerAsync(serverClient, handshake);

        var connected = manager.GetConnectedPeers();
        Assert.Single(connected);
        Assert.Equal(1, manager.ConnectedCount);

        listener.Stop();
        await manager.StopAsync();
        await torrent.DisposeAsync();
        CleanupPath(path);
    }

    private static TorrentFileMetadata CreateMetadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize, Offset = 0 });
        return metadata;
    }

    private static byte[] BuildHandshake(ReadOnlySpan<byte> infoHash, byte[] peerId)
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        infoHash.CopyTo(handshake.AsSpan(28, 20));
        peerId.CopyTo(handshake, 48);
        return handshake;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerManagerInt", Guid.NewGuid().ToString("N"));
    }

    private static void CleanupPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private sealed class FakeGeoIpService : IGeoIpService
    {
        public bool Enabled { get; set; }
        public string GetCountry(IPAddress ip) => "US";
        public void Load(Stream stream) { Enabled = true; }
        public Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            Enabled = true;
            return Task.CompletedTask;
        }
        public void Clear() { Enabled = false; }
    }

    private sealed class RealPeerFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
        {
            return new PeerCommunication(torrent, listener, timeProvider);
        }
    }

    private sealed class FakeConnectionGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public bool TryAcquireConnectionSlot() => true;
        public bool TryAcquirePendingSlot() => true;
        public void ReleaseConnectionSlot() { }
        public void ReleasePendingSlot() { }
    }
}




