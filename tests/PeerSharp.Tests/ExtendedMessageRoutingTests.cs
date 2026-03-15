using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;
using PeerSharp.Messages;

namespace PeerSharp.Tests;

public sealed class ExtendedMessageRoutingTests
{
    [Fact]
    public async Task ExtendedMessageRouting_UsesLocalAssignedId()
    {
        using var tempDir = new TempDirectory();
        var settings = new Settings
        {
            Files = new FilesSettings { DefaultDownloadPath = tempDir.Path }
        };

        var metadata = new TorrentFileMetadata
        {
            Info = new Internals.TorrentFileInfo {
                Hash = InfoHash.Empty,
                FullSize = 0,
                PieceSize = 0,
                Files = new List<Internals.TorrentFileEntry>
                {
                    new Internals.TorrentFileEntry { Path = "test.dat", Size = 0 }
                }
            }
        };

        var bandwidth = new BandwidthManager(1000, TimeProvider.System);
        bandwidth.Start();

        var alerts = new AlertsManager(TimeProvider.System);
        var fileSelectionManager = new FileSelectionManager(metadata);
        var peerFactory = new PeerCommunicationFactory();
        var trackerFactory = new TrackerFactory();
        var geoIp = new GeoIpService();
        var fileHandleCache = new FileHandleCache();
        var connectionGovernor = new ConnectionGovernor(settings);

        var torrent = Torrent.Create(
            metadata,
            settings,
            bandwidth,
            alerts,
            fileSelectionManager,
            peerFactory,
            trackerFactory,
            geoIp,
            fileHandleCache,
            connectionGovernor,
            TimeProvider.System);

        var listener = new TestPeerListener();
        var peer = new PeerCommunication(torrent, listener, TimeProvider.System);

        peer.UtMetadata.SetLocalMessageId(7);

        var handleMethod = typeof(PeerCommunication).GetMethod("HandleExtendedMessageAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handleMethod);

        var task = (Task)handleMethod.Invoke(peer, new object[] { new byte[] { 7 } })!;
        await task;

        Assert.True(listener.ExtendedMessageReceivedCalled);
        Assert.Equal(7, listener.ExtendedMessageType);

        await torrent.DisposeAsync();
        await bandwidth.DisposeAsync();
        fileHandleCache.Dispose();
    }

    private sealed class TestPeerListener : IPeerListener
    {
        public bool ExtendedMessageReceivedCalled { get; private set; }
        public int ExtendedMessageType { get; private set; }

        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;

        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;

        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data)
        {
            ExtendedMessageReceivedCalled = true;
            ExtendedMessageType = type;
            return Task.CompletedTask;
        }

        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;

        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;

        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;

        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;

        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeerSharp.Tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for tests.
            }
        }
    }
}




