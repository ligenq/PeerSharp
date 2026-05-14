using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Reflection;

namespace PeerSharp.Tests.Core.Extensions;

public class UtMetadataRejectTests
{
    private class MockPeer : PeerCommunication
    {
        public List<PeerMessage> Sent { get; } = [];
        public MockPeer(Torrent torrent) : base(torrent, new MockListener(), TimeProvider.System) { }
        public override Task SendMessageAsync(PeerMessage msg)
        {
            Sent.Add(msg);
            return Task.CompletedTask;
        }
    }

    private class MockListener : IPeerListener
    {
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    [Fact]
    public void MetadataRejectReceived_RetriesNextPeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var download = new MetadataDownload(torrent);
        download.Start();

        var p1 = new MockPeer(torrent);
        SetPrivateProperty(p1, "RemoteSupportsExtensions", true);
        var ext1 = new ExtensionHandshake { MetadataSize = 1000 };
        ext1.MessageIds[UtMetadata.Name] = 1;
        SetPrivateProperty(p1, "RemoteExtensions", ext1);
        p1.UtMetadata.Init(ext1);

        var p2 = new MockPeer(torrent);
        SetPrivateProperty(p2, "RemoteSupportsExtensions", true);
        var ext2 = new ExtensionHandshake { MetadataSize = 1000 };
        ext2.MessageIds[UtMetadata.Name] = 1;
        SetPrivateProperty(p2, "RemoteExtensions", ext2);
        p2.UtMetadata.Init(ext2);

        download.PeerConnected(p1);
        download.PeerConnected(p2);

        // P1 should have received request (Request is Extended message)
        Assert.Contains(p1.Sent, m => m.Id == MessageId.Extended);

        // Trigger reject from P1
        download.MetadataRejectReceived(p1, 0);

        // P2 should now receive request
        Assert.Contains(p2.Sent, m => m.Id == MessageId.Extended);
    }

    private static void SetPrivateProperty(object target, string propertyName, object value)
    {
        var backingField = $"<{propertyName}>k__BackingField";
        var type = target.GetType();
        while (type != null)
        {
            var field = type.GetField(backingField, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }
            type = type.BaseType;
        }
        throw new ArgumentException($"Backing field for {propertyName} not found");
    }
}
