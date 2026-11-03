using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Messages waiting in the send queue go out joined into one write, in order and byte for byte as
/// they would have been written alone.
/// </summary>
public class PeerCommunicationSendBatchingTests
{
    [Fact(Timeout = 30000)]
    public async Task QueuedBlocks_AreJoinedIntoFewerWrites_WithTheSameBytes()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        var wire = new RecordingStream();
        peer.Stream = wire;
        typeof(PeerCommunication).GetField("_connected", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(peer, 1);

        var expected = new MemoryStream();
        for (int i = 0; i < 10; i++)
        {
            byte[] data = new byte[ProtocolConstants.BlockSize];
            Array.Fill(data, (byte)i);
            var message = new PeerMessage(MessageId.Piece) { PieceIndex = i, BlockOffset = 0, Data = data };
            byte[] alone = new byte[PeerProtocol.GetMessageLength(message)];
            PeerProtocol.WriteMessage(message, alone);
            expected.Write(alone);
            await peer.SendMessageAsync(message);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = (Task)typeof(PeerCommunication)
            .GetMethod("SendLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(peer, [stop.Token])!;

        while (wire.Length < expected.Length)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await stop.CancelAsync();
        await loop;

        Assert.Equal(expected.ToArray(), wire.ToArray());
        Assert.True(wire.Writes < 10, $"{wire.Writes} writes for 10 queued blocks; they should have been joined.");
    }

    private sealed class RecordingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class NullPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}
