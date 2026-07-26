using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// BEP 3 keepalives, and the relationship between the timeouts that make them necessary.
///
/// <para>
/// A connection with nothing to say still has to say so. A seed serving a peer that is not interested
/// exchanges no messages at all, and a silent connection looks dead: libtorrent drops a peer after
/// <c>peer_timeout</c> of 120 seconds, and Transmission expects traffic on a 100 second cadence. We
/// never sent one, so long-lived idle connections were torn down by the other side.
/// </para>
/// </summary>
public class KeepAliveTests : IDisposable
{
    private readonly string _path;

    public KeepAliveTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpKeepAlive_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void KeepAliveIntervalIsBelowTheTimeoutsOtherClientsUse()
    {
        // libtorrent's peer_timeout is 120s; Transmission keepalives at 100s. Sending less often than
        // that guarantees healthy connections are dropped by the remote.
        Assert.True(
            ProtocolConstants.KeepAliveIntervalMs < 120_000,
            $"Keepalives every {ProtocolConstants.KeepAliveIntervalMs}ms is not often enough: libtorrent drops a " +
            "silent peer after 120000ms.");
    }

    [Fact]
    public void TransportTimeoutOutlivesThePeerIdlePolicy()
    {
        // The transport must not give up before the protocol riding on it. uTP used to close after 60
        // seconds, half the peer-level idle policy, so a quiet peer was killed by the wrong layer and
        // before a remote keepalive at 100 seconds could possibly arrive.
        Assert.True(
            ProtocolConstants.UtpInactivityTimeoutMs > ProtocolConstants.IdleTimeoutMs,
            $"The uTP inactivity timeout ({ProtocolConstants.UtpInactivityTimeoutMs}ms) is not longer than the peer " +
            $"idle timeout ({ProtocolConstants.IdleTimeoutMs}ms), so the transport decides when a peer is dead " +
            "instead of the protocol.");

        Assert.True(
            ProtocolConstants.UtpInactivityTimeoutMs > ProtocolConstants.KeepAliveIntervalMs,
            "The uTP inactivity timeout is not longer than the keepalive interval, so a peer sending keepalives on " +
            "schedule would still be dropped.");
    }

    [Fact(Timeout = 30000)]
    public async Task AnIdlePeerIsSentAKeepAlive()
    {
        var (torrent, manager) = CreateContext();
        try
        {
            var peer = new RecordingPeer(torrent, manager, TimeProvider.System);

            // Look as though we have sent nothing for longer than the keepalive interval.
            peer.SetLastSentTicksForTesting(Environment.TickCount64 - ProtocolConstants.KeepAliveIntervalMs - 5_000);

            var monitor = new PeerHealthMonitor(torrent, NullLogger.Instance);
            await monitor.CheckAsync([peer], connectedCount: 1);

            Assert.Contains(peer.Sent, static id => id == MessageId.KeepAlive);
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ARecentlyActivePeerIsNotSentAKeepAlive()
    {
        // The control: keepalives are for silence, not a heartbeat on every check.
        var (torrent, manager) = CreateContext();
        try
        {
            var peer = new RecordingPeer(torrent, manager, TimeProvider.System);
            peer.SetLastSentTicksForTesting(Environment.TickCount64);

            var monitor = new PeerHealthMonitor(torrent, NullLogger.Instance);
            await monitor.CheckAsync([peer], connectedCount: 1);

            Assert.DoesNotContain(peer.Sent, static id => id == MessageId.KeepAlive);
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    private (Torrent Torrent, PeerManager Manager) CreateContext()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Pieces = [new byte[20]];
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata, _path);
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new PeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor());

        return (torrent, manager);
    }

    private static void Cleanup(Torrent torrent, PeerManager manager)
    {
        try { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
        try { torrent.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }

    private sealed class RecordingPeer : PeerCommunication
    {
        public RecordingPeer(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6881);
            Connected = 1;
        }

        public List<MessageId> Sent { get; } = [];

        public override Task SendMessageAsync(PeerMessage msg)
        {
            Sent.Add(msg.Id);
            return Task.CompletedTask;
        }
    }
}
