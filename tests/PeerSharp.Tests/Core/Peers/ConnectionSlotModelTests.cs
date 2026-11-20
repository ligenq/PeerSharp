using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.Peers;

public sealed class ConnectionSlotModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo PendingEndpoints = typeof(PeerManager).GetField("_pendingConnections",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProcessMessage = typeof(PeerCommunication).GetMethod("ProcessMessageAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task BothTorrentsKeepExactlyTheSlotsTheyOwn(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpSlots-");
        try
        {
            await RunInDirectoryAsync(root.FullName, seed, scenario, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string path, int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var clock = new FakeTimeProvider();
        var settings = new Settings
        {
            // Only explicit discoveries in the history should schedule dials on restart.
            MaxPeersPerTrackerRequest = 0,
            Files = { DefaultDownloadPath = path },
            Connection = { MaxConnections = 2, MaxPendingConnections = 1, ConnectionsPerSecond = 100,
                EnableTcpOut = true, EnableUtpOut = false, Encryption = Encryption.Refuse },
            Dht = { Enabled = false }
        };
        var governor = new ConnectionGovernor(settings);
        var factory = new ScriptedPeerFactory();
        using var handles = new FileHandleCache();
        var aFile = BuildFile("a.bin", isPrivate: false);
        bool isPrivate = (seed & 1) != 0;
        var bFile = BuildFile("b.bin", isPrivate);
        var magnet = new TorrentFileMetadata { Info = new Internals.TorrentFileInfo { Hash = bFile.InfoHash, Name = bFile.Name } };
        await using var a = MakeTorrent(aFile.Metadata, settings, clock, governor, factory, handles);
        await using var b = MakeTorrent(magnet, settings, clock, governor, factory, handles);
        b.MetadataDownload = new MetadataDownload(b);
        await a.StartAsync(ct);
        await b.StartAsync(ct);

        var harness = new Harness(a, b, bFile, governor, factory, clock, scenario);
        await harness.InitializeAsync(ct);
        try
        {
            await harness.RunAsync(Command.Dial, ct);
            // Exercise admission while a dial holds only a pending slot.
            await harness.RunAsync(Command.Incoming, ct);
            await harness.RunAsync(Command.AcceptDial, ct);
            await harness.RunAsync(Command.Duplicate, ct);
            if ((seed & 2) == 0)
            {
                await harness.RunAsync(Command.Rebuild, ct);
                if (isPrivate) await harness.RunAsync(Command.Incoming, ct);
            }
            await harness.RunAsync(Command.RestartDuringClose, ct);
            await harness.RunAsync(Command.Close, ct);
            if ((seed & 2) != 0)
            {
                await harness.RunAsync(Command.Dial, ct);
                await harness.RunAsync(Command.Rebuild, ct);
            }

            await harness.RunAsync(Command.RestartDuringHandshake, ct);
            for (int step = 0; step < 18; step++)
            {
                var choices = harness.Model.Commands().ToArray();
                await harness.RunAsync(choices[scenario.Random.Next(choices.Length)], ct);
            }
        }
        finally
        {
            await harness.DrainAsync(ct);
        }
        await b.StopAsync(ct);
        Assert.Equal(1, governor.ActiveConnections);
        Assert.Equal(0, governor.PendingConnections);
        await a.StopAsync(ct);
        Assert.Equal(0, governor.ActiveConnections);
    }

    private static TorrentFile BuildFile(string name, bool isPrivate)
        => new PeerSharp.Core.TorrentFileBuilder().WithName(name).WithPrivate(isPrivate)
            .WithPieceLength(ProtocolConstants.BlockSize)
            .AddFile(name, Enumerable.Repeat((byte)0x43, ProtocolConstants.BlockSize).ToArray()).Build();

    private static Torrent MakeTorrent(TorrentFileMetadata metadata, Settings settings, TimeProvider clock,
        ConnectionGovernor governor, ScriptedPeerFactory factory, FileHandleCache handles)
        => Torrent.Create(metadata, settings, new TorrentTestUtility.MockBandwidthManager(), new TorrentTestUtility.MockAlertsManager(),
            new FileSelectionManager(metadata), factory, new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(), handles, governor, new HttpClientFactory(), clock);

    private enum Command
    {
        Dial, AcceptDial, RefuseDial, Incoming, Duplicate, BadHandshake, Close, Stop, Start, Rebuild,
        RestartDuringClose, RestartDuringHandshake
    }

    private sealed class OwnershipModel(bool privateMetadata)
    {
        public bool Running { get; private set; } = true;
        public bool Pending { get; private set; }
        public bool Active { get; private set; }
        public bool HasMetadata { get; private set; }
        public int ActiveSlots => Active ? 2 : 1;
        public int PendingSlots => Pending ? 1 : 0;

        public IEnumerable<Command> Commands()
        {
            if (!Running)
            {
                yield return Command.Start;
                yield break;
            }
            yield return Command.Stop;
            if (Pending)
            {
                yield return Command.AcceptDial;
                yield return Command.RefuseDial;
            }
            if (Active)
            {
                yield return Command.Duplicate;
                yield return Command.Close;
                if (!Pending) yield return Command.RestartDuringClose;
            }
            else
            {
                yield return Command.Incoming;
                yield return Command.BadHandshake;
                if (!Pending)
                {
                    yield return Command.Dial;
                    yield return Command.RestartDuringHandshake;
                }
            }
            if (!HasMetadata) yield return Command.Rebuild;
        }

        public void Apply(Command command)
        {
            switch (command)
            {
                case Command.Dial: Pending = true; break;
                case Command.AcceptDial: Pending = false; Active = true; break;
                case Command.RefuseDial: Pending = false; break;
                case Command.Incoming: Active = true; break;
                case Command.Close: Active = false; break;
                case Command.Start: Running = true; break;
                case Command.Stop: Running = false; Pending = false; Active = false; break;
                case Command.Rebuild: HasMetadata = true; Pending = false; Active &= !privateMetadata; break;
            }
        }
    }

    private sealed class Harness(Torrent a, Torrent b, TorrentFile bFile, ConnectionGovernor governor,
        ScriptedPeerFactory factory, FakeTimeProvider clock, ReplayableScenario scenario)
    {
        private PeerCommunication _sentinel = null!;
        private PeerCommunication? _owner;
        private ScriptedDial? _dial;
        private int _nextPort = 10000;

        public OwnershipModel Model { get; } = new(bFile.Metadata.Info.IsPrivate);

        public async Task InitializeAsync(CancellationToken ct)
        {
            _sentinel = await IncomingAsync(a, NewEndpoint(), expected: true, ct)
                ?? throw new InvalidOperationException("The sentinel was not admitted.");
            await CheckAsync(ct);
        }

        public async Task RunAsync(Command command, CancellationToken ct)
        {
            scenario.Record(command.ToString());
            switch (command)
            {
                case Command.Dial:
                    await StartDialAsync(ct);
                    break;
                case Command.AcceptDial:
                case Command.RefuseDial:
                    await FinishDialAsync(command == Command.AcceptDial);
                    break;
                case Command.Incoming:
                    _owner = await IncomingAsync(b, _dial?.RemoteEndPoint ?? NewEndpoint(), expected: true, ct);
                    break;
                case Command.Duplicate:
                    await IncomingAsync(b, _owner!.RemoteEndPoint!, expected: false, ct);
                    break;
                case Command.BadHandshake:
                    var rejected = new IdleStream();
                    await b.PeersInternal.AddIncomingPeerAsync(rejected, [], NewEndpoint());
                    Assert.True(rejected.Closed);
                    break;
                case Command.Close:
                    await _owner!.CloseAsync();
                    _owner = null;
                    break;
                case Command.Stop:
                    await b.StopAsync(ct);
                    await ReturnLateDialAsync(ct);
                    _owner = null;
                    break;
                case Command.Start:
                    await b.StartAsync(ct);
                    break;
                case Command.Rebuild:
                    await RebuildAsync(ct);
                    break;
                case Command.RestartDuringClose:
                    await RestartDuringCloseAsync(ct);
                    break;
                case Command.RestartDuringHandshake:
                    await RestartDuringHandshakeAsync(ct);
                    break;
            }
            Model.Apply(command);
            await CheckAsync(ct);
        }

        private async Task RestartDuringHandshakeAsync(CancellationToken ct)
        {
            // The incoming wire handshake has been read, but the peer is still preparing its
            // extension reply. It has acquired a slot without joining the connected registry.
            var stream = new IdleStream { Handshake = new HandshakeGate() };
            var gate = stream.Handshake;
            var endpoint = NewEndpoint();
            var peers = b.PeersInternal;
            byte[] handshake = PeerHandshake.Create(b.InfoFile.Info, NewPeerId());
            Task admission = peers.AddIncomingPeerAsync(stream, handshake, endpoint);
            try
            {
                await gate.Entered.Task.WaitAsync(Timeout, ct);
                Assert.Equal(2, governor.ActiveConnections);
                Assert.Empty(peers.GetConnectedPeersInternal());
                await b.StopAsync(ct);
                await b.StartAsync(ct);
            }
            finally
            {
                gate.Release.TrySetResult();
                await admission.WaitAsync(Timeout, ct);
            }
            Assert.True(stream.Closed, "A pre-stop handshake must not enter the restarted torrent.");
            Assert.Empty(peers.GetConnectedPeersInternal());
            Assert.Equal(1, governor.ActiveConnections);
            var replacement = await IncomingAsync(b, endpoint, expected: true, ct);
            await replacement!.CloseAsync();
        }

        private async Task StartDialAsync(CancellationToken ct)
        {
            var endpoint = NewEndpoint();
            b.PeersInternal.ConnectTo(endpoint.Address.ToString(), endpoint.Port);
            await TorrentTestUtility.AdvanceUntilAsync(clock, () => factory.Dials.Count > 0,
                TimeSpan.FromMilliseconds(10), because: "the scripted outgoing dial to begin", timeoutMs: 3000);
            Assert.True(factory.Dials.TryDequeue(out var dial));
            await dial.Entered.Task.WaitAsync(Timeout, ct);
            Assert.Equal(endpoint, dial.RemoteEndPoint);
            _dial = dial;
        }

        private async Task FinishDialAsync(bool accepted)
        {
            var dial = _dial!;
            dial.Reply.SetResult(accepted);
            var pending = GetPendingEndpoints(b.PeersInternal);
            await TorrentTestUtility.WaitUntilAsync(() => pending.IsEmpty, because: "the dial to release its pending endpoint");
            if (accepted && !Model.Active) _owner = dial;
            else Assert.Equal(0, dial.Connected);
            _dial = null;
        }

        private async Task ReturnLateDialAsync(CancellationToken ct)
        {
            if (_dial is not { } dial) return;
            _dial = null;
            Assert.True(dial.Token.IsCancellationRequested);
            Assert.Equal(0, governor.PendingConnections);
            dial.Reply.TrySetResult(true);
            await dial.Returned.Task.WaitAsync(Timeout, ct);
            Assert.Equal(0, dial.Connected);
        }

        private async Task RebuildAsync(CancellationToken ct)
        {
            // Use the real metadata decoder and handoff, even if a dial is currently pending.
            byte[] info = bFile.Metadata.InfoBytes!;
            var source = _owner ?? new PeerCommunication(b, b.PeersInternal, clock);
            source.UtMetadata.SetLocalMessageId(1);
            var header = new BDict();
            header.Dict["msg_type"] = new BNumber((int)UtMetadata.MessageType.Data);
            header.Dict["piece"] = new BNumber(0);
            header.Dict["total_size"] = new BNumber(info.Length);
            await b.PeersInternal.ExtendedMessageReceivedAsync(source, 1, [.. BencodeWriter.Write(header), .. info])
                .WaitAsync(Timeout, ct);
            await b.WaitForMetadataAsync(ct).WaitAsync(Timeout, ct);
            await ReturnLateDialAsync(ct);
            if (bFile.Metadata.Info.IsPrivate) _owner = null;
        }

        private async Task RestartDuringCloseAsync(CancellationToken ct)
        {
            var peer = _owner!;
            var endpoint = peer.RemoteEndPoint!;
            if (scenario.Random.Next(2) == 0)
            {
                // Shutdown has begun but has not claimed the peer's registration.
                scenario.Record("remote close wins before the stop drains connections");
                using var releaseStop = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                b.Events = new TorrentEventsBuilder().OnStateChanged((_, state) =>
                {
                    if (state.NewState == TorrentState.Stopping && entered.TrySetResult()) releaseStop.Wait(ct);
                }).Build();
                Task stop = Task.Run(() => b.StopAsync(ct), ct);
                try
                {
                    await entered.Task.WaitAsync(Timeout, ct);
                    await peer.CloseAsync().WaitAsync(Timeout, ct);
                    Assert.Equal(1, governor.ActiveConnections);
                }
                finally
                {
                    releaseStop.Set();
                    await stop.WaitAsync(Timeout, ct);
                    b.Events = null;
                }
                await b.StartAsync(ct);
                _owner = await IncomingAsync(b, endpoint, expected: true, ct);
            }
            else
            {
                // CloseAsync has taken the transport but cannot notify the manager until it is
                // disposed. Stop claims the registration, and the old close callback arrives last.
                scenario.Record("stop releases the slot; old disconnect arrives after replacement");
                var stream = factory.GetStream(peer);
                using var gate = stream.PauseDisposal();
                Task closing = Task.Run(() => peer.CloseAsync(), ct);
                try
                {
                    await gate.Entered.Task.WaitAsync(Timeout, ct);
                    await b.StopAsync(ct).WaitAsync(Timeout, ct);
                    Assert.Equal(1, governor.ActiveConnections);
                    await b.StartAsync(ct);
                    _owner = await IncomingAsync(b, endpoint, expected: true, ct);
                }
                finally
                {
                    gate.Release.Set();
                    await closing.WaitAsync(Timeout, ct);
                }
            }
        }

        private async Task CheckAsync(CancellationToken ct)
        {
            Assert.Equal(Model.Running, b.Started);
            Assert.Equal(Model.HasMetadata, b.HasMetadata);
            Assert.Equal(Model.ActiveSlots, governor.ActiveConnections);
            Assert.Equal(Model.PendingSlots, governor.PendingConnections);
            Assert.Equal(Model.PendingSlots, GetPendingEndpoints(b.PeersInternal).Count);
            Assert.Same(_sentinel, Assert.Single(a.PeersInternal.GetConnectedPeersInternal()));
            Assert.Equal(1, _sentinel.Connected);
            Assert.Equal(Model.Active ? 1 : 0, b.PeersInternal.ConnectedCount);
            if (Model.Active)
            {
                Assert.Same(_owner, Assert.Single(b.PeersInternal.GetConnectedPeersInternal()));
                Assert.Equal(1, _owner!.Connected);
            }
            else
            {
                Assert.Empty(b.PeersInternal.GetConnectedPeersInternal());
            }

            var probe = await IncomingAsync(a, NewEndpoint(), expected: !Model.Active, ct);
            if (probe != null) await probe.CloseAsync().WaitAsync(Timeout, ct);
            Assert.Equal(Model.ActiveSlots, governor.ActiveConnections);
        }

        public async Task DrainAsync(CancellationToken ct)
        {
            if (_dial is not { } dial) return;
            dial.Reply.TrySetResult(false);
            await dial.Returned.Task.WaitAsync(Timeout, ct);
        }

        private async Task<PeerCommunication?> IncomingAsync(Torrent torrent, IPEndPoint endpoint, bool expected, CancellationToken ct)
        {
            var stream = new IdleStream();
            var peers = torrent.PeersInternal;
            await peers.AddIncomingPeerAsync(stream, PeerHandshake.Create(torrent.InfoFile.Info, NewPeerId()), endpoint)
                .WaitAsync(Timeout, ct);
            Assert.Equal(!expected, stream.Closed);
            if (!expected) return null;
            await stream.Reading.Task.WaitAsync(Timeout, ct);
            var peer = Assert.Single(peers.GetConnectedPeersInternal(), p => p.RemoteEndPoint!.Equals(endpoint));
            await ReportAvailabilityAsync(peer);
            return peer;
        }

        private IPEndPoint NewEndpoint() => new(IPAddress.Parse("203.0.113.7"), ++_nextPort);

        private byte[] NewPeerId()
        {
            byte[] peerId = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(peerId, ++_nextPort);
            return peerId;
        }

        private static ConcurrentDictionary<IPEndPoint, long> GetPendingEndpoints(PeerManager peers)
            => (ConcurrentDictionary<IPEndPoint, long>)PendingEndpoints.GetValue(peers)!;
    }

    private sealed class ScriptedPeerFactory : IPeerCommunicationFactory
    {
        private readonly PeerCommunicationFactory _incoming = new();
        private readonly ConditionalWeakTable<PeerCommunication, IdleStream> _streams = new();
        public ConcurrentQueue<ScriptedDial> Dials { get; } = new();

        public IdleStream GetStream(PeerCommunication peer) => _streams.GetValue(peer, _ => throw new InvalidOperationException());

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            var stream = new IdleStream();
            var peer = new ScriptedDial(torrent, listener, timeProvider, stream);
            _streams.Add(peer, stream);
            Dials.Enqueue(peer);
            return peer;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
        {
            var transport = (IdleStream)stream;
            var peer = transport.Handshake is { } gate
                ? new PausedHandshakePeer(torrent, listener, timeProvider, gate) { Connected = 1, RemoteEndPoint = endpoint, Stream = stream }
                : _incoming.Create(torrent, listener, timeProvider, stream, endpoint);
            _streams.Add(peer, transport);
            return peer;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
            => throw new NotSupportedException();
    }

    private sealed class PausedHandshakePeer(Torrent torrent, IPeerListener listener, TimeProvider clock, HandshakeGate gate)
        : PeerCommunication(torrent, listener, clock)
    {
        public override async Task SendMessageAsync(PeerMessage msg)
        {
            using (msg)
            {
                Assert.Equal(MessageId.Extended, msg.Id);
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            }
        }
    }

    private sealed class HandshakeGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScriptedDial(Torrent torrent, IPeerListener listener, TimeProvider clock, IdleStream stream)
        : PeerCommunication(torrent, listener, clock)
    {
        private readonly TorrentFileMetadata _metadata = torrent.InfoFile;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public override async Task<bool> ConnectAsync(string ip, int port, bool useUtp, int timeoutMs,
            bool offerEncryption = true, CancellationToken ct = default)
        {
            IsOutgoing = true;
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            Stream = stream;
            Token = ct;
            Entered.SetResult();
            try
            {
                if (!await Reply.Task || ct.IsCancellationRequested)
                {
                    await CloseAsync();
                    return false;
                }
                var id = new byte[20];
                BinaryPrimitives.WriteInt32BigEndian(id, -port);
                var handshake = PeerHandshake.Create(_metadata.Info, id);
                if (!await SetHandshakeReceivedAsync(handshake)) return false;
                Connected = 1;
                await Listener.HandshakeFinishedAsync(this);
                if (Connected == 1) await ReportAvailabilityAsync(this);
                return true;
            }
            finally
            {
                Returned.TrySetResult();
            }
        }
    }

    private sealed class IdleStream : MemoryStream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DisposeGate? _disposeGate;

        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed => _closed.Task.IsCompleted;
        public HandshakeGate? Handshake { get; init; }

        public DisposeGate PauseDisposal() => _disposeGate = new DisposeGate();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Interlocked.Exchange(ref _disposeGate, null) is { } gate)
                {
                    gate.Entered.TrySetResult();
                    gate.Release.Wait(TimeSpan.FromSeconds(10));
                }
                _closed.TrySetResult();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DisposeGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public void Dispose() => Release.Dispose();
    }

    private static async Task ReportAvailabilityAsync(PeerCommunication peer)
    {
        using var haveNone = new PeerMessage(MessageId.HaveNone);
        await (Task)ProcessMessage.Invoke(peer, [haveNone])!;
        Assert.True(peer.HasReportedPieces);
    }
}
