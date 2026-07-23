using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
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
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new PeerCommunicationFactory(), timeProvider, new TorrentTestUtility.MockConnectionGovernor());

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

    [Fact(Timeout = 30000)]
    public async Task AddConnectedPeerAsync_AttachesInitiatorAndResponderStreams()
    {
        string leftPath = CreateTempPath();
        string rightPath = CreateTempPath();
        var metadata = CreateMetadata();

        var leftTorrent = TorrentTestUtility.CreateMinimal(metadata, leftPath);
        var rightTorrent = TorrentTestUtility.CreateMinimal(metadata, rightPath);
        leftTorrent.Settings.Connection.Encryption = Encryption.Refuse;
        rightTorrent.Settings.Connection.Encryption = Encryption.Refuse;

        var timeProvider = new FakeTimeProvider();
        var leftManager = new PeerManager(leftTorrent, new TorrentTestUtility.MockGeoIpService(), new PeerCommunicationFactory(), timeProvider, new TorrentTestUtility.MockConnectionGovernor());
        var rightManager = new PeerManager(rightTorrent, new TorrentTestUtility.MockGeoIpService(), new PeerCommunicationFactory(), timeProvider, new TorrentTestUtility.MockConnectionGovernor());

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var leftClient = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await leftClient.ConnectAsync(IPAddress.Loopback, port);
        using var rightClient = await acceptTask;

        await leftManager.AddConnectedPeerAsync(leftClient.GetStream(), initiator: true, remote: (IPEndPoint?)leftClient.Client.RemoteEndPoint, sourceKind: PeerSourceKind.WebTorrent);
        await rightManager.AddConnectedPeerAsync(rightClient.GetStream(), initiator: false, remote: (IPEndPoint?)rightClient.Client.RemoteEndPoint, sourceKind: PeerSourceKind.WebTorrent);

        await AssertEventuallyAsync(() => leftManager.ConnectedCount == 1 && rightManager.ConnectedCount == 1, TimeSpan.FromSeconds(5));

        listener.Stop();
        await leftManager.StopAsync();
        await rightManager.StopAsync();
        await leftTorrent.DisposeAsync();
        await rightTorrent.DisposeAsync();
        CleanupPath(leftPath);
        CleanupPath(rightPath);
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

    private static async Task AssertEventuallyAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(predicate());
    }

}




