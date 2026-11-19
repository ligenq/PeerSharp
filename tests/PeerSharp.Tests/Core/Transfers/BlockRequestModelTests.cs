using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Transfers;

public sealed class BlockRequestModelTests
{
    private const int BlockSize = ProtocolConstants.BlockSize;
    private const int HardTimeoutMs = 100;
    private static readonly FieldInfo Requests = PrivateField<FileTransfer>("_requestTracker");
    private static readonly FieldInfo Timeouts = PrivateField<FileTransfer>("_requestTimeoutManager");
    private static readonly FieldInfo SendQueue = PrivateField<PeerCommunication>("_sendQueue");
    private static readonly FieldInfo MutationLock = PrivateField<BlockRequestTracker>("_mutationLock");
    private static readonly MethodInfo ProcessMessage = typeof(PeerCommunication).GetMethod("ProcessMessageAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AHealthyPeerCanFinishAfterRequestOwnershipChanges(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static FieldInfo PrivateField<T>(string name) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpRequestModel-");
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
        var payload = new byte[BlockSize + scenario.Random.Next(1, BlockSize + 1)];
        scenario.Random.NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("handoff.bin").WithPieceLength(2 * BlockSize)
            .AddFile("handoff.bin", payload).Build();
        var clock = new FakeTimeProvider();
        await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, path, timeProvider: clock);
        torrent.Settings.Transfer.MinBlockHardTimeoutMs = HardTimeoutMs;
        torrent.Settings.Transfer.MaxBlockHardTimeoutMs = HardTimeoutMs;
        await torrent.StartAsync(ct);
        int publications = 0;
        torrent.Events = new TorrentEventsBuilder().OnPieceCompleted((_, piece) =>
        {
            Assert.Equal(0, piece.PieceIndex);
            Interlocked.Increment(ref publications);
        }).Build();

        bool fastB = (seed & 1) != 0;
        scenario.Record(fastB ? "B negotiated the Fast Extension" : "B uses the base protocol");
        var harness = new Harness(torrent, clock, payload, fastB, scenario);
        await harness.RunAsync(new(Verb.Connect, 0));
        await harness.RunAsync(new(Verb.Connect, 1));
        if ((seed & 2) != 0) await harness.ChokeDuringSchedulingAsync(ct);
        await harness.RunAsync(new(Verb.Unchoke, 0));
        await harness.RunAsync(new(Verb.Expire));
        await harness.RunAsync(new(Verb.Unchoke, 1));
        await harness.RunAsync(new(Verb.LateReply, 0, seed % 2));
        await harness.RunAsync(new(Verb.Choke, 0));
        await harness.RunAsync(new(Verb.Choke, 1));
        if (fastB)
        {
            // A Fast choke leaves requests alive. B rejects one and sends the other after choking.
            int rejected = (seed >> 1) & 1;
            await harness.RunAsync(new(Verb.Reject, 1, rejected));
            await harness.RunAsync(new(Verb.Reply, 1, 1 - rejected));
        }
        await harness.RunAsync(new(Verb.Unchoke, 0));

        for (int step = 0; step < 24; step++)
        {
            var choices = harness.AvailableCommands().ToArray();
            await harness.RunAsync(choices[scenario.Random.Next(choices.Length)]);
        }

        scenario.Record("make A healthy and answer every block it is asked for");
        await harness.RestoreHealthySourceAsync();
        await TorrentTestUtility.WaitUntilAsync(() => Volatile.Read(ref publications) == 1 && !harness.Transfer.IsPieceActive(0),
            because: "the piece to verify and publish");
        await harness.VerifyHaveAsync(ct);
        harness.Check();
        Assert.True(torrent.Pieces.HasPiece(0));
        Assert.Equal(payload.Length, harness.Transfer.Downloader.Downloaded);
        await torrent.StopAsync(ct);
        Assert.Equal(1, Volatile.Read(ref publications));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(path, torrent.GetFileInfo(0).Path), ct));
    }

    [Fact(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    public async Task ABlockReceivedBeforeChokeSurvivesTheProcessingQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("PeerSharpBlockBeforeChoke-");
        try
        {
            var clock = new FakeTimeProvider();
            var payload = Enumerable.Repeat((byte)0x72, 2 * BlockSize).ToArray();
            var file = new PeerSharp.Core.TorrentFileBuilder().WithName("queued.bin").WithPieceLength(2 * BlockSize)
                .AddFile("queued.bin", payload).Build();
            using var gate = new BlockProcessingGate(ct);
            await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, root.FullName,
                timeProvider: clock, loggerFactory: gate);
            await torrent.StartAsync(ct);
            var harness = new Harness(torrent, clock, payload, fastB: false, new ReplayableScenario(0));
            await harness.RunAsync(new(Verb.Connect, 0));
            await harness.RunAsync(new(Verb.Connect, 1));
            await harness.RunAsync(new(Verb.Unchoke, 0));

            try
            {
                await harness.QueueBlockBeforeChokeAsync(gate, ct);
            }
            finally
            {
                gate.Release.Set();
            }

            await harness.RestoreHealthySourceAsync();
            await harness.VerifyHaveAsync(ct);
            harness.Check();
            Assert.True(torrent.Pieces.HasPiece(0));
            await torrent.StopAsync(ct);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(root.FullName, "queued.bin"), ct));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private enum Verb { Connect, Disconnect, Unchoke, Choke, Evaluate, Expire, Reply, LateReply, Reject }

    private readonly record struct Command(Verb Verb, int Peer = 0, int Block = 0)
    {
        public override string ToString()
        {
            string peer = Peer switch { 0 => "A", 1 => "B", 2 => "retired A", _ => "retired B" };
            return Verb switch
            {
                Verb.Expire => "Expire outstanding requests",
                Verb.Reply or Verb.LateReply or Verb.Reject => $"{Verb} from {peer} for block {Block}",
                _ => $"{Verb} {peer}"
            };
        }
    }

    private sealed class Harness(Torrent torrent, FakeTimeProvider clock, byte[] payload, bool fastB, ReplayableScenario scenario)
    {
        private readonly PeerCommunication?[] _peers = new PeerCommunication?[2];
        private readonly PeerCommunication?[] _retired = new PeerCommunication?[2];
        private readonly bool[] _unchoked = new bool[2];
        private readonly int?[] _owner = new int?[2];
        private readonly bool[] _received = new bool[2];
        private readonly BlockRequestTracker _tracker = (BlockRequestTracker)Requests.GetValue(torrent.FileTransferInternal)!;
        private readonly RequestTimeoutManager _timeouts = (RequestTimeoutManager)Timeouts.GetValue(torrent.FileTransferInternal)!;
        private int _expired;
        private int _nextId;

        public FileTransfer Transfer { get; } = torrent.FileTransferInternal;

        public async Task ChokeDuringSchedulingAsync(CancellationToken ct)
        {
            scenario.Record("choke A after its scheduling pass starts the piece but before it can register a request");
            var mutationLock = (Lock)MutationLock.GetValue(_tracker)!;
            using var release = new ManualResetEventSlim();
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task lockOwner = Task.Run(() =>
            {
                lock (mutationLock)
                {
                    held.SetResult();
                    release.Wait(ct);
                }
            }, ct);

            var peer = _peers[0]!;
            Task scheduling = Task.CompletedTask;
            Task choking = Task.CompletedTask;
            peer.SetPeerChokingForTesting(false);
            try
            {
                await held.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                scheduling = Task.Run(() => Transfer.EvaluateNextRequestsAsync(peer, immediate: true), ct);
                await TorrentTestUtility.WaitUntilAsync(() => Transfer.IsPieceActive(0), because: "A to start the piece");
                choking = Task.Run(() => ReceiveAsync(peer, new PeerMessage(MessageId.Choke)), ct);
                await TorrentTestUtility.WaitUntilAsync(() => peer.PeerChoking, because: "A to report its choke");
            }
            finally
            {
                release.Set();
                await Task.WhenAll(lockOwner, scheduling, choking).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            DrainMessages(peer, []);
            Check();
        }

        public async Task QueueBlockBeforeChokeAsync(BlockProcessingGate gate, CancellationToken ct)
        {
            var sender = _peers[0]!;
            // Pin the worker on a different, unsolicited block so the valid one really stays in
            // the channel until after choke. A lookup at the worker's entry would also be too late.
            await Transfer.BlockReceivedAsync(_peers[1]!, new Block(0, 0, Length(0)));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var block = new Block(0, 0, Length(0));
            payload.AsSpan(0, Length(0)).CopyTo(block.Buffer);
            await ReceiveAsync(sender, new PeerMessage(MessageId.Piece) { PooledBlock = block });
            await RunAsync(new(Verb.Choke, 0));
            gate.Release.Set();
            await TorrentTestUtility.WaitUntilAsync(() => Transfer.Downloader.Downloaded == Length(0) || block.Data.IsEmpty,
                because: "the already-queued block to be processed");
            Assert.Equal(Length(0), Transfer.Downloader.Downloaded);
            _received[0] = true;
            Check();
        }

        public IEnumerable<Command> AvailableCommands()
        {
            // Keep all explicit timeout steps before the engine's first one-second tick.
            if (_expired < 7 && _owner.Any(owner => owner.HasValue)) yield return new(Verb.Expire);
            for (int peer = 0; peer < _peers.Length; peer++)
            {
                if (_peers[peer] == null)
                {
                    yield return new(Verb.Connect, peer);
                }
                else
                {
                    yield return new(_unchoked[peer] ? Verb.Choke : Verb.Unchoke, peer);
                    yield return new(Verb.Evaluate, peer);
                    yield return new(Verb.Disconnect, peer);
                    for (int block = 0; block < _owner.Length; block++)
                    {
                        if (_owner[block] != peer)
                        {
                            yield return new(Verb.LateReply, peer, block);
                        }
                        else
                        {
                            // Leave at least one block for the final liveness check.
                            if (!_received.Contains(true)) yield return new(Verb.Reply, peer, block);
                            if (IsFast(peer) && !_unchoked[peer] && !_unchoked[1 - peer]) yield return new(Verb.Reject, peer, block);
                        }
                    }
                }
                if (_retired[peer] != null)
                {
                    yield return new(Verb.LateReply, peer + _peers.Length);
                }
            }
        }

        public async Task RunAsync(Command command)
        {
            scenario.Record(command.ToString());
            int id = command.Peer;
            int block = command.Block;
            var peer = id < _peers.Length ? _peers[id] : _retired[id - _peers.Length];
            switch (command.Verb)
            {
                case Verb.Connect:
                    await ConnectAsync(id);
                    break;
                case Verb.Disconnect:
                    await peer!.CloseAsync();
                    DrainMessages(peer, []);
                    Release(id);
                    _retired[id] = peer;
                    _peers[id] = null;
                    _unchoked[id] = false;
                    break;
                case Verb.Unchoke:
                    _unchoked[id] = true;
                    await EvaluateAsync(id, sendUnchoke: true);
                    break;
                case Verb.Choke:
                    await ReceiveAsync(peer!, new PeerMessage(MessageId.Choke));
                    _unchoked[id] = false;
                    if (!IsFast(id)) Release(id);
                    break;
                case Verb.Evaluate:
                    await EvaluateAsync(id, sendUnchoke: false);
                    break;
                case Verb.Expire:
                    clock.Advance(TimeSpan.FromMilliseconds(HardTimeoutMs + 1));
                    _timeouts.ProcessTimeouts(clock.GetUtcNow(), endGameMode: false);
                    for (int i = 0; i < _owner.Length; i++)
                    {
                        if (_owner[i] is { } expired) Assert.True(_peers[expired]!.IsSnubbed);
                    }
                    Array.Clear(_owner);
                    _expired++;
                    break;
                case Verb.Reply:
                case Verb.LateReply:
                    bool expected = command.Verb == Verb.Reply;
                    await DeliverAsync(peer!, block, expected);
                    if (expected)
                    {
                        _owner[block] = null;
                        _received[block] = true;
                    }
                    break;
                case Verb.Reject:
                    await ReceiveAsync(peer!, new PeerMessage(MessageId.Reject)
                    {
                        PieceIndex = 0,
                        BlockOffset = block * BlockSize,
                        BlockLength = Length(block)
                    });
                    _owner[block] = null;
                    break;
            }
            Check();
        }

        public async Task RestoreHealthySourceAsync()
        {
            if (_peers[1] != null) await RunAsync(new(Verb.Disconnect, 1));
            if (_peers[0] == null) await RunAsync(new(Verb.Connect, 0));
            await RunAsync(new(_unchoked[0] ? Verb.Evaluate : Verb.Unchoke, 0));
            for (int block = 0; block < _received.Length; block++)
            {
                if (_received[block]) continue;
                Assert.Equal(0, _owner[block]);
                await RunAsync(new(Verb.Reply, 0, block));
            }
        }

        public async Task VerifyHaveAsync(CancellationToken ct)
        {
            var queue = (MessageQueue)SendQueue.GetValue(_peers[0]!)!;
            int haveCount = 0;
            await TorrentTestUtility.WaitUntilAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                while (queue.TryDequeue(out var message))
                {
                    using (message)
                    {
                        if (message.Id != MessageId.Have) continue;
                        Assert.Equal(0, message.HavePieceIndex);
                        haveCount++;
                    }
                }
                return haveCount != 0;
            }, because: "A to receive the HAVE announcement");
            Assert.Equal(1, haveCount);
        }

        public void Check()
        {
            Assert.Equal(_owner.Count(owner => owner.HasValue), _tracker.BlockRequestIndexCount);
            int expectedBytes = 0;
            for (int block = 0; block < _owner.Length; block++)
            {
                var key = (0, block * BlockSize);
                if (_received[block]) expectedBytes += Length(block);
                if (_owner[block] is not { } owner)
                {
                    Assert.False(_tracker.TryGetBlockPeers(key, out _));
                    continue;
                }
                var peer = _peers[owner]!;
                Assert.True(_tracker.TryGetBlockPeers(key, out var reverse));
                var indexed = Assert.Single(reverse);
                Assert.Same(peer, indexed.Key);
                Assert.True(_tracker.TryGetPeerRequests(peer, out var forward));
                Assert.True(forward.TryGetValue(key, out var request));
                Assert.Same(request, indexed.Value);
                Assert.Equal((0, block * BlockSize, Length(block)), (request.PieceIndex, request.Offset, request.Length));
            }

            Assert.Equal(expectedBytes, Transfer.Downloader.Downloaded);
            Assert.Equal(_owner.Count(owner => owner.HasValue),
                _tracker.EnumeratePeerRequests().Values.Sum(requests => requests.Count));
            if (_received.Any(received => !received)) Assert.False(torrent.Pieces.HasPiece(0));
        }

        private async Task ConnectAsync(int id)
        {
            string address = id == 0 ? "203.0.113.8" : "203.0.113.9";
            var peer = new PeerCommunication(torrent, torrent.PeersInternal, clock)
            {
                Connected = 1,
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 6881)
            };
            var peerId = new byte[20];
            peerId[0] = (byte)++_nextId;
            byte[] handshake = PeerHandshake.Create(torrent.InfoFile.Info, peerId);
            handshake[25] = 0;
            handshake[27] = IsFast(id) ? (byte)4 : (byte)0;
            Assert.True(await peer.SetHandshakeReceivedAsync(handshake));
            peer.PeerPieces.AddPiece(0);
            torrent.PeersInternal.AddConnectedPeerForTesting(peer);
            Transfer.RegisterPeerAvailability(peer);
            _peers[id] = peer;
            _unchoked[id] = false;
        }

        private async Task EvaluateAsync(int id, bool sendUnchoke)
        {
            var requested = new List<int>();
            if (_unchoked[id])
            {
                for (int block = 0; block < _owner.Length; block++)
                {
                    if (_received[block] || _owner[block].HasValue) continue;
                    _owner[block] = id;
                    requested.Add(block);
                }
            }

            var peer = _peers[id]!;
            if (sendUnchoke)
            {
                await ReceiveAsync(peer, new PeerMessage(MessageId.Unchoke));
            }
            else
            {
                await Transfer.EvaluateNextRequestsAsync(peer, immediate: true);
            }
            DrainMessages(peer, requested);
        }

        private void DrainMessages(PeerCommunication peer, IReadOnlyList<int> expected)
        {
            var sent = new List<(int Piece, int Offset, int Length)>();
            var queue = (MessageQueue)SendQueue.GetValue(peer)!;
            while (queue.TryDequeue(out var message))
            {
                using (message)
                {
                    if (message.Id == MessageId.Request) sent.Add((message.PieceIndex, message.BlockOffset, message.BlockLength));
                }
            }
            Assert.Equal(expected.Select(block => (0, block * BlockSize, Length(block))), sent);
        }

        private async Task DeliverAsync(PeerCommunication peer, int index, bool expected)
        {
            int offset = index * BlockSize;
            int length = Length(index);
            var block = new Block(0, offset, length);
            payload.AsSpan(offset, length).CopyTo(block.Buffer);
            if (!expected) block.Buffer[0] ^= 0xff;
            await Transfer.ProcessBlockAsync(peer, block);
            if (!expected) Assert.True(block.Data.IsEmpty);
        }

        private void Release(int peer)
        {
            for (int i = 0; i < _owner.Length; i++)
            {
                if (_owner[i] == peer) _owner[i] = null;
            }
        }

        private int Length(int block) => Math.Min(BlockSize, payload.Length - block * BlockSize);
        private bool IsFast(int peer) => peer == 1 && fastB;
    }

    private sealed class BlockProcessingGate(CancellationToken ct) : ILoggerFactory, ILogger
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(BlockProcessor).FullName ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Trace;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Trace && Entered.TrySetResult()) Release.Wait(ct);
        }

        public void Dispose() => Release.Dispose();
    }

    private static async Task ReceiveAsync(PeerCommunication peer, PeerMessage message)
    {
        using (message)
        {
            await (Task)ProcessMessage.Invoke(peer, [message])!;
        }
    }
}
