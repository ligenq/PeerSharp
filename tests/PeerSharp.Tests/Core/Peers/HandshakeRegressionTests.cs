using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Tests.Core.Peers;

public class HandshakeRegressionTests
{
    private static readonly byte[] RemoteId = Encoding.ASCII.GetBytes("-ZZ0001-123456789012");
    private static readonly byte[] Unchoke = [0, 0, 0, 1, 1];

    [Theory(Timeout = 10000)]
    [InlineData(Encryption.Allow, false, false, true)]
    [InlineData(Encryption.Allow, true, false, false)]
    [InlineData(Encryption.Allow, false, true, true)]
    [InlineData(Encryption.Require, false, false, false)]
    public async Task PlaintextReplyToMseHonorsPolicyAndValidatesHandshake(Encryption policy, bool wrongHash, bool fragmented, bool expected)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.Settings.Connection.Encryption = policy;
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var connect = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, false, 5000);
        try
        {
            using var remote = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            byte[] key = new byte[96];
            await remote.GetStream().ReadExactlyAsync(key, TestContext.Current.CancellationToken);
            byte[] handshake = Handshake(torrent);
            if (wrongHash)
            {
                handshake[28] ^= 1;
            }
            if (fragmented)
            {
                await remote.GetStream().WriteAsync(handshake.AsMemory(0, 1), TestContext.Current.CancellationToken);
                await Task.Delay(30, TestContext.Current.CancellationToken);
                await remote.GetStream().WriteAsync(handshake.AsMemory(1), TestContext.Current.CancellationToken);
            }
            else
            {
                await remote.GetStream().WriteAsync(handshake.Concat(Unchoke).ToArray(), TestContext.Current.CancellationToken);
            }

            Assert.Equal(expected, await connect.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(expected ? 1 : 0, callbacks.Handshakes);
            if (expected)
            {
                Assert.Equal(RemoteId, peer.PeerId);
                if (!fragmented)
                {
                    Assert.Equal(MessageId.Unchoke, await callbacks.Message.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            await peer.CloseAsync();
            await connect;
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OutgoingHandshakeHonorsWholeAttemptDeadlineAndCancellation(bool encrypted, bool cancel)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.Settings.Connection.Encryption = encrypted ? Encryption.Require : Encryption.Refuse;
        var peer = new PeerCommunication(torrent, new Listener(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var connect = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, false, cancel ? 5000 : 500, ct: cancellation.Token);
        try
        {
            using var remote = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            byte[] first = new byte[1];
            await remote.GetStream().ReadExactlyAsync(first, TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
            }
            Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal(0, peer.Connected);
        }
        finally
        {
            await peer.CloseAsync();
            await connect;
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(68, true)]
    [InlineData(73, true)]
    [InlineData(0, false)]
    [InlineData(20, false)]
    [InlineData(68, false)]
    public async Task IncomingMseCompletesPartialPayloadAndPreservesPipelinedMessages(int initialLength, bool pipeline)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        byte[] handshake = Handshake(torrent);
        byte[] payload = [.. handshake, .. Unchoke];
        using var initiator = new ProtocolEncryptionHandshake(torrent.Hash.ToArray(), true)
        {
            InitialPayload = payload[..initialLength]
        };
        using var stream = new MseStream(initiator, payload[initialLength..], pipeline);
        var result = await IncomingHandshakeNegotiator.NegotiateAsync(stream, new Resolver(torrent), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(handshake, result.Handshake[..68]);
        Assert.NotNull(result.Encryption);
        // A fixed-size handshake read may leave the message on the stream; coalesced reads
        // carry it in the returned prefix instead. In either case the next message is intact.
        byte[] message = new byte[5];
        int buffered = result.Handshake.Length - 68;
        result.Handshake.AsSpan(68).CopyTo(message);
        if (buffered < message.Length)
        {
            await stream.ReadExactlyAsync(message.AsMemory(buffered), TestContext.Current.CancellationToken);
            result.Encryption.Decrypt(message.AsSpan(buffered));
        }
        Assert.Equal(Unchoke, message);
        byte[] next = [0, 0, 0, 1, 2];
        initiator.Encryption!.Encrypt(next);
        result.Encryption.Decrypt(next);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 2 }, next);

        var peer = new PeerCommunication(torrent, new Listener(), TimeProvider.System);
        try
        {
            Assert.True(await peer.SetHandshakeReceivedAsync(result.Handshake));
            Assert.Equal(RemoteId, peer.PeerId);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    [Fact(Timeout = 10000)]
    public async Task FragmentedHandshakeDoesNotRestartOutgoingAttemptDeadline()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.Settings.Connection.Encryption = Encryption.Refuse;
        var peer = new PeerCommunication(torrent, new Listener(), TimeProvider.System);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        // The handshake trickles in over 6.8 seconds: an attempt whose deadline restarted with each byte
        // would still be connecting when the wait below gives up. The deadline leaves a loaded runner time
        // to accept and read the first byte before it passes - at 750 ms one did not, and the reset that
        // ended the attempt took the unread byte with it.
        var connect = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, false, 2000);
        using var remote = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        byte[] first = new byte[1];
        await remote.GetStream().ReadExactlyAsync(first, TestContext.Current.CancellationToken);
        var sender = SendFragmentsAsync();
        try
        {
            Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            await peer.CloseAsync();
            remote.Dispose();
            await sender;
            await connect;
        }

        async Task SendFragmentsAsync()
        {
            try
            {
                foreach (byte value in Handshake(torrent))
                {
                    await remote.GetStream().WriteAsync(new byte[] { value }, TestContext.Current.CancellationToken);
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(68, true)]
    [InlineData(0, false)]
    [InlineData(20, false)]
    [InlineData(68, false)]
    public async Task AttachedIncomingMseCompletesPartialHandshakeBeforeProcessingMessages(int initialLength, bool pipeline)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        byte[] payload = [.. Handshake(torrent), .. Unchoke];
        using var initiator = new ProtocolEncryptionHandshake(torrent.Hash.ToArray(), true)
        {
            InitialPayload = payload[..initialLength]
        };
        using var stream = new MseStream(initiator, payload[initialLength..], pipeline);
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        try
        {
            peer.Start(stream);
            Assert.Equal(MessageId.Unchoke, await callbacks.Message.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(1, callbacks.Handshakes);
            Assert.Equal(RemoteId, peer.PeerId);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData((int)TorrentVersion.V2, true)]
    [InlineData((int)TorrentVersion.Hybrid, true)]
    [InlineData((int)TorrentVersion.Hybrid, false)]
    public async Task IncomingMseResolvesV2AndHybridWireHashes(int versionValue, bool useV2)
    {
        var version = (TorrentVersion)versionValue;
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.InfoFile.Info.Version = version;
        torrent.InfoFile.Info.HashV2 = new InfoHash(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        torrent.InfoFile.Info.Hash = version == TorrentVersion.V2 ? InfoHash.Empty : InfoHash.CreateRandom();
        byte[] wireHash = (useV2 ? torrent.HashV2.TruncateToV1() : torrent.Hash).ToArray();
        byte[] handshake = Handshake(torrent);
        wireHash.CopyTo(handshake, 28);
        using var initiator = new ProtocolEncryptionHandshake(wireHash, true) { InitialPayload = handshake };
        using var stream = new MseStream(initiator, [], true);
        var result = await IncomingHandshakeNegotiator.NegotiateAsync(stream, new Resolver(torrent), NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new InfoHash(wireHash), result.InfoHash);
        Assert.True(PeerHandshake.TryParse(result.Handshake, torrent.InfoFile.Info, out _));
    }

    [Fact(Timeout = 10000)]
    public async Task OutgoingV2OnlyConnectionUsesTruncatedV2HashForMse()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.InfoFile.Info.Version = TorrentVersion.V2;
        torrent.InfoFile.Info.HashV2 = InfoHash.CreateRandomV2();
        torrent.Settings.Connection.Encryption = Encryption.Require;
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var connect = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, false, 5000);
        try
        {
            using var remote = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var mse = new ProtocolEncryptionHandshake(torrent.HashV2.TruncateToV1().ToArray(), false);
            byte[] buffer = new byte[4096];
            while (!mse.IsComplete && !mse.IsError)
            {
                int read = await remote.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken);
                Assert.True(read > 0);
                byte[] response = mse.HandleIncoming(buffer[..read]);
                if (mse.IsComplete)
                {
                    byte[] handshake = Handshake(torrent);
                    mse.Encryption!.Encrypt(handshake);
                    response = [.. response, .. handshake];
                }
                await remote.GetStream().WriteAsync(response, TestContext.Current.CancellationToken);
            }
            Assert.False(mse.IsError);
            Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(RemoteId, peer.PeerId);
            Assert.True(peer.RemoteSupportsV2);
        }
        finally
        {
            await peer.CloseAsync();
            await connect;
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(Encryption.Require, false, false, false)]
    [InlineData(Encryption.Require, false, true, false)]
    [InlineData(Encryption.Refuse, true, false, false)]
    [InlineData(Encryption.Refuse, true, true, false)]
    [InlineData(Encryption.Allow, false, false, true)]
    [InlineData(Encryption.Allow, false, true, true)]
    [InlineData(Encryption.Allow, true, false, true)]
    [InlineData(Encryption.Allow, true, true, true)]
    [InlineData(Encryption.Require, true, false, true)]
    [InlineData(Encryption.Require, true, true, true)]
    [InlineData(Encryption.Refuse, false, false, true)]
    [InlineData(Encryption.Refuse, false, true, true)]
    public async Task IncomingAdmissionEnforcesEncryptionPolicy(Encryption policy, bool encrypted, bool tcp, bool expected)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.Settings.Connection.Encryption = policy;
        var governor = new ConnectionGovernor(torrent.Settings);
        await using var manager = new PeerManager(torrent, new TorrentTestUtility.MockGeoIpService(), new PeerCommunicationFactory(), TimeProvider.System, governor);
        using var sockets = await SocketPair.CreateAsync();
        ProtocolEncryption? encryption = encrypted ? CreateEncryption() : null;
        if (tcp)
        {
            if (encryption != null)
            {
                await manager.AddIncomingPeerAsync(sockets.Server, Handshake(torrent), encryption);
            }
            else
            {
                await manager.AddIncomingPeerAsync(sockets.Server, Handshake(torrent));
            }
        }
        else
        {
            await manager.AddIncomingPeerAsync(sockets.Server.GetStream(), Handshake(torrent), new IPEndPoint(IPAddress.Loopback, 6001), encryption);
        }
        Assert.Equal(expected ? 1 : 0, manager.ConnectedCount);
        Assert.Equal(expected ? 1 : 0, governor.ActiveConnections);
    }

    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(28)]
    public async Task AttachedIncomingMseRejectsInvalidBitTorrentHandshake(int corruptedByte)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        byte[] handshake = Handshake(torrent);
        handshake[corruptedByte] ^= 1;
        using var initiator = new ProtocolEncryptionHandshake(torrent.Hash.ToArray(), true) { InitialPayload = handshake };
        using var stream = new MseStream(initiator, [], true);
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        try
        {
            peer.Start(stream);
            await callbacks.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(0, callbacks.Handshakes);
            Assert.Equal(0, peer.Connected);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(Encryption.Require, false, false)]
    [InlineData(Encryption.Refuse, true, false)]
    [InlineData(Encryption.Require, true, true)]
    [InlineData(Encryption.Refuse, false, true)]
    public async Task StartingWithAPrevalidatedHandshakeStillEnforcesPolicy(Encryption policy, bool encrypted, bool expected)
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        torrent.Settings.Connection.Encryption = policy;
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        using var stream = new MemoryStream();
        try
        {
            Assert.True(await peer.SetHandshakeReceivedAsync(Handshake(torrent)));
            peer.Start(stream, encrypted ? CreateEncryption() : null);
            await callbacks.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(expected ? 1 : 0, callbacks.Handshakes);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    [Fact(Timeout = 10000)]
    public async Task InitialPayloadMessagesReachIncomingPeerReceiveLoop()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(downloadPath: Path.GetTempPath());
        using var initiator = new ProtocolEncryptionHandshake(torrent.Hash.ToArray(), true)
        {
            InitialPayload = [.. Handshake(torrent), .. Unchoke]
        };
        using var stream = new MseStream(initiator, [], true);
        var result = await IncomingHandshakeNegotiator.NegotiateAsync(stream, new Resolver(torrent), NullLogger.Instance, TestContext.Current.CancellationToken);
        var callbacks = new Listener();
        var peer = new PeerCommunication(torrent, callbacks, TimeProvider.System);
        try
        {
            Assert.True(await peer.SetHandshakeReceivedAsync(result.Handshake));
            peer.Start(stream, result.Encryption);
            Assert.Equal(MessageId.Unchoke, await callbacks.Message.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(1, callbacks.Handshakes);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    private static byte[] Handshake(Torrent torrent)
    {
        byte[] handshake = PeerHandshake.Create(torrent.InfoFile.Info, RemoteId);
        handshake[25] = 0; // Keep these tests independent of extension negotiation.
        return handshake;
    }

    private static ProtocolEncryption CreateEncryption()
    {
        var encryption = new ProtocolEncryption();
        encryption.RC4In.Init("test encryption"u8);
        encryption.RC4Out.Init("test encryption"u8);
        return encryption;
    }

    private sealed class Resolver(ITorrent torrent) : ITorrentResolver
    {
        public ITorrent? GetTorrent(InfoHash hash) => torrent.Hash == hash || torrent.HashV2.TruncateToV1() == hash ? torrent : null;
        public IReadOnlyList<ITorrent> GetTorrents() => [torrent];
    }

    // A deterministic duplex exchange keeps Pe3 and following messages in a single read when
    // requested, instead of relying on the operating system to coalesce loopback TCP writes.
    private sealed class MseStream(ProtocolEncryptionHandshake initiator, byte[] following, bool pipeline) : Stream
    {
        private readonly Queue<byte> _input = new(initiator.Initiate());
        private bool _sentPayload;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, _input.Count);
            for (int i = 0; i < count; i++)
            {
                buffer.Span[i] = _input.Dequeue();
            }
            return ValueTask.FromResult(count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (initiator.IsComplete)
            {
                return ValueTask.CompletedTask;
            }
            byte[] response = initiator.HandleIncoming(buffer.ToArray());
            if (response.Length > 0)
            {
                Enqueue(response);
                if (pipeline)
                {
                    SendPayload();
                }
            }
            if (initiator.IsComplete && !pipeline)
            {
                SendPayload();
            }
            return ValueTask.CompletedTask;
        }

        private void SendPayload()
        {
            if (!_sentPayload)
            {
                _sentPayload = true;
                byte[] data = following.ToArray();
                initiator.Encryption!.Encrypt(data);
                Enqueue(data);
            }
        }

        private void Enqueue(byte[] data)
        {
            foreach (byte value in data)
            {
                _input.Enqueue(value);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SocketPair(TcpClient client, TcpClient server) : IDisposable
    {
        public TcpClient Server { get; } = server;
        public static async Task<SocketPair> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
            var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await connect;
            return new SocketPair(client, server);
        }
        public void Dispose()
        {
            client.Dispose();
            Server.Dispose();
        }
    }

    private sealed class Listener : IPeerListener
    {
        public int Handshakes;
        public TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MessageId> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task HandshakeFinishedAsync(IPeerCommunication peer)
        {
            Interlocked.Increment(ref Handshakes);
            return Task.CompletedTask;
        }
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code)
        {
            Closed.TrySetResult(true);
            return Task.CompletedTask;
        }
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage message)
        {
            Message.TrySetResult(message.Id);
            return Task.CompletedTask;
        }
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> flags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort port) => Task.CompletedTask;
    }
}
