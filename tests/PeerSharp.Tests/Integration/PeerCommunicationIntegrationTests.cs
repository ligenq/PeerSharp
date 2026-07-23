using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public class PeerCommunicationIntegrationTests
{
    [Fact(Timeout = 30000)]
    public async Task HandshakeAndInterestedMessage_AcrossTcp()
    {
        var metadata = CreateMetadata();
        string pathA = CreateTempPath("A");
        string pathB = CreateTempPath("B");

        var torrentA = TorrentTestUtility.CreateMinimal(CloneMetadata(metadata), pathA);
        var torrentB = TorrentTestUtility.CreateMinimal(CloneMetadata(metadata), pathB);

        torrentA.Settings.Connection.Encryption = Encryption.Refuse;
        torrentB.Settings.Connection.Encryption = Encryption.Refuse;

        var serverListener = new TestPeerListener();
        var clientListener = new TestPeerListener();

        var serverPeer = new PeerCommunication(torrentA, serverListener, TimeProvider.System);
        var clientPeer = new PeerCommunication(torrentB, clientListener, TimeProvider.System);

        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;

        var acceptTask = Task.Run(async () =>
        {
            var client = await tcpListener.AcceptTcpClientAsync();
            serverPeer.Start(client.GetStream());
        });

        try
        {
            bool connected = await clientPeer.ConnectAsync("127.0.0.1", port, useUtp: false, timeoutMs: 5000);
            Assert.True(connected);

            await Task.WhenAll(serverListener.HandshakeReceived.Task, clientListener.HandshakeReceived.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await clientPeer.SendMessageAsync(new PeerMessage(MessageId.Interested));

            var msg = await serverListener.ReceivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MessageId.Interested, msg.Id);
        }
        finally
        {
            tcpListener.Stop();
            await clientPeer.CloseAsync();
            await serverPeer.CloseAsync();
            await torrentA.DisposeAsync();
            await torrentB.DisposeAsync();
            CleanupPath(pathA);
            CleanupPath(pathB);
            await acceptTask;
        }
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

    private static TorrentFileMetadata CloneMetadata(TorrentFileMetadata source)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = source.Info.Version;
        metadata.Info.Hash = source.Info.Hash;
        metadata.Info.HashV2 = source.Info.HashV2;
        metadata.Info.PieceSize = source.Info.PieceSize;
        metadata.Info.FullSize = source.Info.FullSize;
        foreach (var file in source.Info.Files)
        {
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = file.Path, Size = file.Size, Offset = file.Offset });
        }
        return metadata;
    }

    private static string CreateTempPath(string suffix)
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_PeerCommInt_" + suffix, Guid.NewGuid().ToString("N"));
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

    private sealed class TestPeerListener : IPeerListener
    {
        public TaskCompletionSource<bool> HandshakeReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PeerMessage> ReceivedMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandshakeFinishedAsync(IPeerCommunication peer)
        {
            HandshakeReceived.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg)
        {
            if (msg.Id == MessageId.Interested)
            {
                ReceivedMessage.TrySetResult(msg);
            }
            return Task.CompletedTask;
        }

        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}




