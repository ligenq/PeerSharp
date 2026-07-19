using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Verifies that PeerManager never keeps two live PeerCommunication instances for the same
/// remote endpoint: duplicate incoming connections are rejected, an outgoing dial that loses
/// a race against an incoming connection is dropped, IPv4-mapped IPv6 endpoints are normalized
/// so both forms dedup against each other, and a closing duplicate cannot evict the surviving
/// connection's endpoint registration.
/// </summary>
public class PeerManagerDuplicateConnectionTests
{
    private static readonly byte[] TestInfoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();

    [Fact]
    public void NormalizeEndPoint_MapsIPv4MappedIPv6ToPlainIPv4()
    {
        var mapped = new IPEndPoint(IPAddress.Parse("::ffff:1.2.3.4"), 6881);
        var normalized = NetworkUtils.NormalizeEndPoint(mapped);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881), normalized);
    }

    [Fact]
    public void NormalizeEndPoint_LeavesPlainAddressesUntouched()
    {
        var v4 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);

        Assert.Same(v4, NetworkUtils.NormalizeEndPoint(v4));
        Assert.Same(v6, NetworkUtils.NormalizeEndPoint(v6));
        Assert.Null(NetworkUtils.NormalizeEndPoint(null));
    }

    [Fact]
    public void RemoteEndPoint_IsNormalizedOnAssignment()
    {
        var ctx = CreateContext();
        try
        {
            var peer = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System)
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("::ffff:1.2.3.4"), 6881)
            };

            Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881), peer.RemoteEndPoint);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AddIncomingPeer_SecondConnectionFromSameEndpoint_IsRejected()
    {
        var ctx = CreateContext();
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);

            var stream1 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream1, BuildHandshake(), endpoint);

            Assert.Equal(1, ctx.Manager.ConnectedCount);
            Assert.Equal(endpoint, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);

            var stream2 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream2, BuildHandshake(), endpoint);

            // The duplicate must be rejected and its stream closed; the original stays connected.
            Assert.Equal(1, ctx.Manager.ConnectedCount);
            Assert.Equal(endpoint, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);
            Assert.True(stream2.Disposed);
            Assert.False(stream1.Disposed);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AddIncomingPeer_MappedIPv6AndPlainIPv4_AreTreatedAsSameEndpoint()
    {
        var ctx = CreateContext();
        try
        {
            // Dual-stack listeners report IPv4 peers as ::ffff:a.b.c.d
            var mapped = new IPEndPoint(IPAddress.Parse("::ffff:9.8.7.6"), 6881);
            var plain = new IPEndPoint(IPAddress.Parse("9.8.7.6"), 6881);

            await ctx.Manager.AddIncomingPeerAsync(new HangingStream(), BuildHandshake(), mapped);

            // The reported endpoint is the normalized IPv4 form
            Assert.Equal(plain, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);

            // A second connection using the plain form must dedup against the mapped one
            var stream2 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream2, BuildHandshake(), plain);

            Assert.Equal(1, ctx.Manager.ConnectedCount);
            Assert.True(stream2.Disposed);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AddConnectedPeer_SecondConnectionFromSameEndpoint_IsRejected()
    {
        var ctx = CreateContext();
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse("4.3.2.1"), 51413);

            await ctx.Manager.AddConnectedPeerAsync(new HangingStream(), initiator: false, remote: endpoint);
            Assert.Equal(1, ctx.Manager.ConnectedCount);

            var stream2 = new HangingStream();
            await ctx.Manager.AddConnectedPeerAsync(stream2, initiator: false, remote: endpoint);

            Assert.Equal(1, ctx.Manager.ConnectedCount);
            Assert.True(stream2.Disposed);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task ConnectionClosed_ForNonOwningPeer_DoesNotEvictSurvivorsEndpoint()
    {
        var ctx = CreateContext();
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);

            await ctx.Manager.AddIncomingPeerAsync(new HangingStream(), BuildHandshake(), endpoint);
            Assert.Equal(1, ctx.Manager.ConnectedCount);

            // Simulate a rejected duplicate (same endpoint, never registered) reporting its close.
            // It must not remove the surviving connection's endpoint registration.
            var ghost = new PeerCommunication(ctx.Torrent, ctx.Manager, TimeProvider.System)
            {
                RemoteEndPoint = endpoint
            };
            await ctx.Manager.ConnectionClosedAsync(ghost, 0);

            Assert.Equal(1, ctx.Manager.ConnectedCount);

            // If the ghost had evicted the endpoint entry, this third connection would be accepted
            // and we would end up with two live connections for the same endpoint.
            var stream3 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream3, BuildHandshake(), endpoint);

            Assert.Equal(1, ctx.Manager.ConnectedCount);
            Assert.Single(ctx.Manager.GetConnectedPeers());
            Assert.True(stream3.Disposed);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task OutgoingConnection_CompletingAfterIncomingFromSameEndpoint_IsDropped()
    {
        var factory = new RacingPeerFactory();
        var ctx = CreateContext(factory);
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse("5.6.7.8"), 6881);

            await ctx.Manager.StartAsync();

            // Queue the outgoing dial and wait until it is in flight (ConnectAsync awaited)
            ctx.Manager.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
            var outgoing = await WaitForAsync(() => factory.LastOutgoing is { ConnectStarted: true } p ? p : null);

            // While the dial is in flight, the same peer connects to us
            await ctx.Manager.AddIncomingPeerAsync(new HangingStream(), BuildHandshake(), endpoint);
            Assert.Equal(1, ctx.Manager.ConnectedCount);

            // Now the outgoing dial completes - it lost the race and must be closed
            outgoing.ConnectGate.SetResult(true);
            await WaitForAsync(() => outgoing.CloseCalls > 0 ? (object)outgoing : null);

            Assert.Equal(1, ctx.Manager.ConnectedCount);
            var connected = Assert.Single(ctx.Manager.GetConnectedPeersInternal());
            Assert.NotSame(outgoing, connected);
            Assert.Equal(endpoint, connected.RemoteEndPoint);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AddIncomingPeer_SamePeerIdAtDifferentEndpoint_IsRejected()
    {
        var ctx = CreateContext();
        try
        {
            byte[] peerId = MakePeerId(0x42);
            var endpoint1 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50001);
            var endpoint2 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50002);

            await ctx.Manager.AddIncomingPeerAsync(new HangingStream(), BuildHandshake(peerId), endpoint1);
            await WaitForPeerIdClaimsAsync(ctx.Manager, 1);

            // Same peer id reconnecting from a different source port: same-direction duplicate,
            // the existing connection wins and the new one is closed.
            var stream2 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream2, BuildHandshake(peerId), endpoint2);

            await WaitForAsync(() => ctx.Manager.ConnectedCount == 1 && stream2.Disposed ? (object)this : null);
            Assert.Equal(endpoint1, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task AddIncomingPeer_SamePeerIdAtDifferentEndpoint_ReplacesIdleExistingConnection()
    {
        var ctx = CreateContext();
        try
        {
            byte[] peerId = MakePeerId(0x42);
            var endpoint1 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50001);
            var endpoint2 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50002);
            var stream1 = new HangingStream();

            await ctx.Manager.AddIncomingPeerAsync(stream1, BuildHandshake(peerId), endpoint1);
            await WaitForPeerIdClaimsAsync(ctx.Manager, 1);

            var existing = Assert.Single(ctx.Manager.GetConnectedPeersInternal());
            existing.SetLastActivityTicksForTesting(Environment.TickCount64 - ProtocolConstants.IdleTimeoutMs - 1);

            var stream2 = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream2, BuildHandshake(peerId), endpoint2);

            await WaitForAsync(() =>
            {
                var peer = ctx.Manager.GetConnectedPeers().SingleOrDefault();
                return peer != null &&
                    ctx.Manager.ConnectedCount == 1 &&
                    stream1.Disposed &&
                    !stream2.Disposed &&
                    endpoint2.Equals(peer.EndPoint)
                        ? (object)this
                        : null;
            });
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task OutgoingConnection_SamePeerIdAtDifferentEndpoint_ReplacesIdleExistingConnection()
    {
        var factory = new RacingPeerFactory();
        var ctx = CreateContext(factory);
        try
        {
            byte[] peerId = MakePeerId(0x42);
            var endpoint1 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50001);
            var endpoint2 = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50002);

            await ctx.Manager.StartAsync();

            ctx.Manager.ConnectTo(endpoint1.Address.ToString(), endpoint1.Port);
            var outgoing1 = await WaitForAsync(() => factory.LastOutgoing is { ConnectStarted: true } p ? p : null);
            outgoing1.HandshakePeerId = peerId;
            outgoing1.ConnectGate.SetResult(true);

            await WaitForAsync(() =>
                ctx.Manager.ConnectedCount == 1 &&
                ReferenceEquals(ctx.Manager.GetConnectedPeersInternal().SingleOrDefault(), outgoing1)
                    ? (object)this
                    : null);
            outgoing1.SetLastActivityTicksForTesting(Environment.TickCount64 - ProtocolConstants.IdleTimeoutMs - 1);

            ctx.Manager.ConnectTo(endpoint2.Address.ToString(), endpoint2.Port);
            var outgoing2 = await WaitForAsync(() =>
                factory.LastOutgoing is { ConnectStarted: true } p && !ReferenceEquals(p, outgoing1) ? p : null);
            outgoing2.HandshakePeerId = peerId;
            outgoing2.ConnectGate.SetResult(true);

            await WaitForAsync(() =>
                outgoing1.CloseCalls > 0 &&
                ctx.Manager.ConnectedCount == 1 &&
                ReferenceEquals(ctx.Manager.GetConnectedPeersInternal().SingleOrDefault(), outgoing2)
                    ? (object)this
                    : null);
            Assert.Equal(endpoint2, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    [Fact]
    public async Task CrossedConnections_LocalIdSmaller_OutgoingConnectionWins()
    {
        await RunCrossedConnectionTestAsync(localIdByte: 0x01, remoteIdByte: 0x7F, expectOutgoingWins: true);
    }

    [Fact]
    public async Task CrossedConnections_LocalIdLarger_IncomingConnectionWins()
    {
        await RunCrossedConnectionTestAsync(localIdByte: 0x7F, remoteIdByte: 0x01, expectOutgoingWins: false);
    }

    [Fact]
    public async Task SelfConnection_IsDetectedAndClosed()
    {
        var ctx = CreateContext();
        try
        {
            byte[] localId = MakePeerId(0x33);
            Array.Copy(localId, ctx.Torrent.Settings.PeerId, 20);

            var stream = new HangingStream();
            await ctx.Manager.AddIncomingPeerAsync(stream, BuildHandshake(localId), new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881));

            // The handshake carries our own peer id - the connection must be torn down
            await WaitForAsync(() => ctx.Manager.ConnectedCount == 0 && stream.Disposed ? (object)this : null);
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    private async Task RunCrossedConnectionTestAsync(byte localIdByte, byte remoteIdByte, bool expectOutgoingWins)
    {
        var factory = new RacingPeerFactory();
        var ctx = CreateContext(factory);
        try
        {
            Array.Copy(MakePeerId(localIdByte), ctx.Torrent.Settings.PeerId, 20);
            byte[] remoteId = MakePeerId(remoteIdByte);

            var incomingEndpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 50001);
            var outgoingEndpoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);

            await ctx.Manager.StartAsync();

            // The peer connects to us first (incoming, from its ephemeral port)...
            await ctx.Manager.AddIncomingPeerAsync(new HangingStream(), BuildHandshake(remoteId), incomingEndpoint);
            await WaitForPeerIdClaimsAsync(ctx.Manager, 1);

            // ...while our outgoing dial to its listen port is in flight
            ctx.Manager.ConnectTo(outgoingEndpoint.Address.ToString(), outgoingEndpoint.Port);
            var outgoing = await WaitForAsync(() => factory.LastOutgoing is { ConnectStarted: true } p ? p : null);
            outgoing.HandshakePeerId = remoteId;
            outgoing.ConnectGate.SetResult(true);

            if (expectOutgoingWins)
            {
                // Both sides must converge on the connection initiated by the smaller peer id -
                // here ours, so the incoming connection is replaced by the outgoing one.
                await WaitForAsync(() =>
                    ctx.Manager.ConnectedCount == 1 && ReferenceEquals(ctx.Manager.GetConnectedPeersInternal().SingleOrDefault(), outgoing)
                        ? (object)this : null);
                Assert.Equal(outgoingEndpoint, Assert.Single(ctx.Manager.GetConnectedPeers()).EndPoint);
            }
            else
            {
                // The incoming connection wins and our outgoing dial is dropped
                await WaitForAsync(() => outgoing.CloseCalls > 0 ? (object)this : null);
                await WaitForAsync(() => ctx.Manager.ConnectedCount == 1 ? (object)this : null);
                var survivor = Assert.Single(ctx.Manager.GetConnectedPeersInternal());
                Assert.NotSame(outgoing, survivor);
                Assert.Equal(incomingEndpoint, survivor.RemoteEndPoint);
            }
        }
        finally
        {
            Cleanup(ctx);
        }
    }

    private static byte[] MakePeerId(byte fill)
    {
        var id = new byte[20];
        id.AsSpan().Fill(fill);
        return id;
    }

    private static Task WaitForPeerIdClaimsAsync(PeerManager manager, int expectedCount)
    {
        return TorrentTestUtility.WaitUntilAsync(
            () => manager.ConnectedPeerIdCountForTesting == expectedCount,
            because: $"peer id registrations to reach {expectedCount}");
    }

    private static byte[] BuildHandshake(byte[]? peerId = null)
    {
        var handshake = new byte[68];
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));
        // 8 reserved bytes left zero: no extension protocol, so no writes during handshake processing
        TestInfoHash.CopyTo(handshake.AsSpan(28));
        if (peerId != null)
        {
            peerId.CopyTo(handshake.AsSpan(48));
        }
        else
        {
            handshake.AsSpan(48).Fill((byte)'A'); // arbitrary peer id
        }

        return handshake;
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe() is { } result)
            {
                return result;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for condition");
        return null!; // unreachable
    }

    private static TestContext CreateContext(IPeerCommunicationFactory? factory = null)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(TestInfoHash);
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        string path = Path.Combine(Path.GetTempPath(), "PeerSharpTests_DuplicateConnections", Guid.NewGuid().ToString("N"));
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);

        factory ??= new PeerCommunicationFactory();
        var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), factory, TimeProvider.System, new TorrentTestUtility.MockConnectionGovernor());
        return new TestContext(torrent, manager, path);
    }

    private static void Cleanup(TestContext ctx)
    {
        ctx.Manager.StopAsync().GetAwaiter().GetResult();
        ctx.Torrent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(ctx.Path))
            {
                Directory.Delete(ctx.Path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }

    private sealed record TestContext(Torrent Torrent, PeerManager Manager, string Path);

    /// <summary>
    /// A stream whose reads never complete (until cancelled or disposed), so peers started on it
    /// stay alive for the duration of a test instead of hitting EOF and disconnecting.
    /// </summary>
    private sealed class HangingStream : Stream
    {
        private readonly CancellationTokenSource _disposeCts = new();

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return 0; // EOF after disposal
            }

            return 0; // unreachable when cancelled via caller token (exception propagates)
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _disposeCts.Token.WaitHandle.WaitOne();
            return 0; // EOF after disposal
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                _disposeCts.Cancel();
                _disposeCts.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Outgoing peer whose ConnectAsync blocks on a gate, letting the test establish an
    /// incoming connection to the same endpoint while the dial is "in flight".
    /// </summary>
    private sealed class RacingOutgoingPeer : PeerCommunication
    {
        private int _closeCalls;

        public RacingOutgoingPeer(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
            : base(torrent, listener, timeProvider)
        {
        }

        public TaskCompletionSource<bool> ConnectGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ConnectStarted { get; private set; }
        public int CloseCalls => Volatile.Read(ref _closeCalls);

        /// <summary>
        /// When set, the mock behaves like a completed real handshake: it copies this id into
        /// PeerId and raises HandshakeFinishedAsync before ConnectAsync returns, mirroring
        /// the real ConnectAsync flow.
        /// </summary>
        public byte[]? HandshakePeerId { get; set; }

        public override async Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs, CancellationToken ct = default)
        {
            IsOutgoing = true;
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            ConnectStarted = true;
            bool success = await ConnectGate.Task;
            if (success)
            {
                Connected = 1;
                if (HandshakePeerId != null)
                {
                    Array.Copy(HandshakePeerId, PeerId, 20);
                    try { await Listener.HandshakeFinishedAsync(this); } catch { /* mirrors real flow: callback errors are swallowed */ }
                }
            }

            return success;
        }

        public override Task CloseAsync()
        {
            Interlocked.Increment(ref _closeCalls);
            bool wasConnected = Connected == 1;
            Connected = 0;
            if (wasConnected)
            {
                return Listener.ConnectionClosedAsync(this, 0);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RacingPeerFactory : IPeerCommunicationFactory
    {
        private readonly PeerCommunicationFactory _real = new();

        public RacingOutgoingPeer? LastOutgoing { get; private set; }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            LastOutgoing = new RacingOutgoingPeer(torrent, listener, timeProvider);
            return LastOutgoing;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
        {
            return _real.Create(torrent, listener, timeProvider, stream, endpoint);
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
        {
            return _real.Create(torrent, listener, timeProvider, client);
        }
    }
}
