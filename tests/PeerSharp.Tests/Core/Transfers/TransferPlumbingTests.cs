using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using System.Buffers;
using System.Net;

namespace PeerSharp.Tests.Core.Transfers;

public class RequestQueuePolicyTests
{
    [Theory]
    [InlineData(0, 5, 16, 0)]      // nothing left to request
    [InlineData(-1, 5, 16, 0)]     // over budget already
    [InlineData(10, 0, 16, 0)]     // no room for another piece
    [InlineData(10, 5, 16, 1)]     // one piece covers ten blocks
    [InlineData(16, 5, 16, 1)]     // exactly one piece
    [InlineData(17, 5, 16, 2)]     // one block over rounds up
    [InlineData(160, 5, 16, 5)]    // demand exceeds the slots available
    public void OnlyAsManyPiecesAreOpenedAsTheRemainingRequestsCanFill(
        int remainingRequestSlots, int activePieceSlots, int blocksPerPiece, int expected)
    {
        // Opening a piece per request would scatter a peer's queue over dozens of partial pieces,
        // each of which has to be completed before any of it can be verified.
        Assert.Equal(
            expected,
            RequestQueuePolicy.CalculateNewPieceStartLimit(remainingRequestSlots, activePieceSlots, blocksPerPiece));
    }

    [Fact]
    public void ANonsensicalBlockCountIsTreatedAsOneBlockPerPieceRatherThanDividingByZero()
    {
        // A zero piece length reaches this from a torrent whose metadata has not arrived. The floor
        // of one keeps the division defined; the answer is then bounded by the slots available,
        // which is the same cap every other input is subject to.
        Assert.Equal(5, RequestQueuePolicy.CalculateNewPieceStartLimit(10, 5, 0));
        Assert.Equal(5, RequestQueuePolicy.CalculateNewPieceStartLimit(10, 5, -3));
        Assert.Equal(3, RequestQueuePolicy.CalculateNewPieceStartLimit(3, 9, 0));
    }

    [Fact]
    public void AtLeastOnePieceIsOpenedWhenThereIsAnySlotAndAnyDemand()
    {
        // The floor matters: rounding could otherwise produce zero for a single outstanding slot,
        // and a scheduler that opens no piece requests nothing and stalls.
        Assert.Equal(1, RequestQueuePolicy.CalculateNewPieceStartLimit(1, 1, 1000));
    }
}

public class UploadQueueItemTests
{
    [Fact]
    public void TheThreeFieldsAreCarriedThroughAndConvertToABlockRequest()
    {
        var item = new UploadQueueItem(pieceIndex: 7, offset: 16384, length: 4096);

        Assert.Equal(7, item.PieceIndex);
        Assert.Equal(16384, item.Offset);
        Assert.Equal(4096, item.Length);

        var request = item.ToBlockRequest();
        Assert.Equal(7, request.PieceIndex);
        Assert.Equal(16384, request.Offset);
        Assert.Equal(4096, request.Length);
    }

    [Fact]
    public void ADefaultItemIsAllZeroesRatherThanUndefined()
    {
        UploadQueueItem item = default;

        Assert.Equal(0, item.PieceIndex);
        Assert.Equal(0, item.Offset);
        Assert.Equal(0, item.Length);
    }
}

public class PieceVerificationOutcomeTests
{
    [Fact]
    public void ASuccessCarriesTheDataAndItsSize()
    {
        byte[] data = new byte[64];
        using var outcome = new PieceVerificationOutcome(
            hashSuccess: true, hashFailed: false, pieceSize: 64, fullData: data, pool: null);

        Assert.True(outcome.HashSuccess);
        Assert.False(outcome.HashFailed);
        Assert.Equal(64, outcome.PieceSize);
        Assert.Same(data, outcome.FullData);
    }

    [Fact]
    public void AFailureCarriesNoData()
    {
        using var outcome = new PieceVerificationOutcome(
            hashSuccess: false, hashFailed: true, pieceSize: 0, fullData: null, pool: null);

        Assert.False(outcome.HashSuccess);
        Assert.True(outcome.HashFailed);
        Assert.Null(outcome.FullData);
    }

    [Fact]
    public void DisposeReturnsARentedBufferAndIsSafeToRepeat()
    {
        // Piece buffers are rented per verification and are megabytes each, so failing to return
        // one is a leak measured in pieces per second rather than in bytes.
        var pool = ArrayPool<byte>.Shared;
        byte[] rented = pool.Rent(1024);

        var outcome = new PieceVerificationOutcome(true, false, 1024, rented, pool);

        outcome.Dispose();
        outcome.Dispose();

        Assert.Null(outcome.FullData);
    }

    [Fact]
    public void DisposeWithoutAPoolLeavesTheCallersArrayAlone()
    {
        byte[] data = new byte[8];
        var outcome = new PieceVerificationOutcome(true, false, 8, data, pool: null);

        outcome.Dispose();

        Assert.Equal(8, data.Length);
    }
}

public class PeerRequestCollectionTests
{
    [Fact]
    public void AddingAndRemovingKeepsTheCountInStep()
    {
        var collection = new PeerRequestCollection();

        Assert.True(collection.IsEmpty);
        Assert.Equal(0, collection.Count);

        collection[(1, 0)] = Request(1, 0);
        collection[(1, 16384)] = Request(1, 16384);

        Assert.Equal(2, collection.Count);
        Assert.False(collection.IsEmpty);

        Assert.True(collection.TryRemove((1, 0), out var removed));
        Assert.Equal(0, removed.Offset);
        Assert.Equal(1, collection.Count);
    }

    [Fact]
    public void OverwritingAKeyDoesNotDoubleCountIt()
    {
        // The count is maintained alongside the dictionary rather than read from it, so a replace
        // that incremented would drift the peer's outstanding-request figure upward forever.
        var collection = new PeerRequestCollection();

        collection[(1, 0)] = Request(1, 0);
        collection[(1, 0)] = Request(1, 0, length: 999);

        Assert.Equal(1, collection.Count);
        Assert.True(collection.TryGetValue((1, 0), out var replaced));
        Assert.Equal(999, replaced.Length);
    }

    [Fact]
    public void RemovingAKeyThatWasNeverThereChangesNothing()
    {
        var collection = new PeerRequestCollection();
        collection[(1, 0)] = Request(1, 0);

        Assert.False(collection.TryRemove((2, 0), out _));
        Assert.Equal(1, collection.Count);

        Assert.False(collection.TryRemove((1, 0), out _) && collection.TryRemove((1, 0), out _));
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void TryGetValueReportsWhetherTheRequestIsOutstanding()
    {
        var collection = new PeerRequestCollection();
        collection[(3, 16384)] = Request(3, 16384);

        Assert.True(collection.TryGetValue((3, 16384), out var found));
        Assert.Equal(3, found.PieceIndex);
        Assert.False(collection.TryGetValue((3, 0), out _));
    }

    [Fact]
    public void KeysValuesAndTheEnumerableViewAgree()
    {
        var collection = new PeerRequestCollection();
        collection[(1, 0)] = Request(1, 0);
        collection[(2, 0)] = Request(2, 0);

        Assert.Equal(2, collection.Keys.Count);
        Assert.Equal(2, collection.Values.Count);
        Assert.Equal(2, collection.AsEnumerable().Count);
        Assert.Contains((1, 0), collection.Keys);
    }

    [Fact]
    public void TheCountSurvivesConcurrentAddsAndRemoves()
    {
        var collection = new PeerRequestCollection();

        Parallel.For(0, 500, i => collection[(i, 0)] = Request(i, 0));
        Assert.Equal(500, collection.Count);

        Parallel.For(0, 500, i => collection.TryRemove((i, 0), out _));
        Assert.Equal(0, collection.Count);
        Assert.True(collection.IsEmpty);
    }

    private static BlockRequest Request(int piece, int offset, int length = 16384) =>
        new() { PieceIndex = piece, Offset = offset, Length = length };
}

public class RequestCompletionTrackerTests
{
    [Fact]
    public void AnArrivingBlockClearsItsRequestAndTeachesThePeerItsRoundTrip()
    {
        // The round trip measured here is what sizes the peer's pipeline and its block timeouts, so
        // a completion that forgot to record it would leave both stuck at their starting estimates.
        var clock = new FakeTimeProvider();
        var torrent = TorrentTestUtility.CreateMinimal();
        var tracker = new BlockRequestTracker();
        var removed = new List<(int Piece, int Offset)>();
        var completion = new RequestCompletionTracker(tracker, clock, (p, o, _) => removed.Add((p, o)));

        var peer = new PeerCommunication(torrent, new NullPeerListener(), clock);
        int before = peer.SmoothedRttMs;

        var request = new BlockRequest
        {
            PieceIndex = 1,
            Offset = 0,
            Length = 16384,
            Timestamp = clock.GetUtcNow()
        };
        tracker.GetOrAddPeerRequests(peer)[(1, 0)] = request;
        tracker.AddBlockRequest(1, 0, peer, request);

        clock.Advance(TimeSpan.FromMilliseconds(400));
        completion.HandleBlockReceived(peer, new Block(1, 0, 16384));

        Assert.Equal([(1, 0)], removed);
        Assert.True(tracker.TryGetPeerRequests(peer, out var remaining));
        Assert.Equal(0, remaining.Count);
        Assert.NotEqual(before, peer.SmoothedRttMs);
    }

    [Fact]
    public void ABlockNobodyAskedForIsIgnoredRatherThanCounted()
    {
        // Peers do send unsolicited blocks, and a cancel that raced the data arriving looks exactly
        // like one. Neither should move the round-trip estimate.
        var clock = new FakeTimeProvider();
        var torrent = TorrentTestUtility.CreateMinimal();
        var tracker = new BlockRequestTracker();
        var removed = new List<(int, int)>();
        var completion = new RequestCompletionTracker(tracker, clock, (p, o, _) => removed.Add((p, o)));

        var peer = new PeerCommunication(torrent, new NullPeerListener(), clock);
        int before = peer.SmoothedRttMs;

        completion.HandleBlockReceived(peer, new Block(5, 0, 16384));

        Assert.Empty(removed);
        Assert.Equal(before, peer.SmoothedRttMs);
    }

    [Fact]
    public void ARequestWithNoTimestampIsCompletedWithoutMovingTheRoundTrip()
    {
        // A request restored across a restart has no send time. Treating MinValue as an instant
        // would record a round trip of several centuries.
        var clock = new FakeTimeProvider();
        var torrent = TorrentTestUtility.CreateMinimal();
        var tracker = new BlockRequestTracker();
        var completion = new RequestCompletionTracker(tracker, clock, (_, _, _) => { });

        var peer = new PeerCommunication(torrent, new NullPeerListener(), clock);
        int before = peer.SmoothedRttMs;

        tracker.GetOrAddPeerRequests(peer)[(2, 0)] = new BlockRequest
        {
            PieceIndex = 2,
            Offset = 0,
            Length = 16384,
            Timestamp = DateTimeOffset.MinValue
        };

        completion.HandleBlockReceived(peer, new Block(2, 0, 16384));

        Assert.Equal(before, peer.SmoothedRttMs);
    }

    [Fact]
    public void TryGetPendingRequestFindsOnlyWhatThisPeerOwes()
    {
        var clock = new FakeTimeProvider();
        var torrent = TorrentTestUtility.CreateMinimal();
        var tracker = new BlockRequestTracker();
        var completion = new RequestCompletionTracker(tracker, clock, (_, _, _) => { });

        var asked = new PeerCommunication(torrent, new NullPeerListener(), clock);
        var other = new PeerCommunication(torrent, new NullPeerListener(), clock);
        tracker.GetOrAddPeerRequests(asked)[(4, 0)] = new BlockRequest { PieceIndex = 4, Offset = 0, Length = 16384 };

        Assert.True(completion.TryGetPendingRequest(asked, new Block(4, 0, 16384), out var found));
        Assert.Equal(4, found.PieceIndex);

        Assert.False(completion.TryGetPendingRequest(other, new Block(4, 0, 16384), out _));
        Assert.False(completion.TryGetPendingRequest(asked, new Block(4, 16384, 16384), out _));
    }
}

public class StandardBlockRequestStrategyTests
{
    [Fact]
    public void ABlockAlreadyHeldIsNotRequestedAgain()
    {
        var fixture = new StrategyFixture();
        var state = new PieceState(1, 4);
        state.Blocks[0] = true;

        Assert.False(fixture.Standard.IsBlockRequestable(state, 1, 0, fixture.Peer, isPeerFast: true));
    }

    [Fact]
    public void AnUnrequestedBlockIsRequestableByAnyone()
    {
        var fixture = new StrategyFixture();

        Assert.True(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: false));
    }

    [Fact]
    public void ASlowPeerNeverDuplicatesABlockSomebodyElseOwes()
    {
        // Duplicating onto a slow peer buys nothing: the copy arrives after the original, and the
        // bandwidth spent could have fetched a block nobody has.
        var fixture = new StrategyFixture();
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.OtherPeer, ageMs: 100000);

        Assert.False(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: false));
    }

    [Fact]
    public void AFastPeerDuplicatesOnlyOnceTheBlockHasPassedTheSoftTimeout()
    {
        var fixture = new StrategyFixture(softTimeoutMs: 3000);
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.OtherPeer, ageMs: 1000);

        Assert.False(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: true));

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(5000));
        Assert.True(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: true));
    }

    [Fact]
    public void APeerIsNotAskedTwiceForTheSameBlock()
    {
        var fixture = new StrategyFixture(softTimeoutMs: 100);
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.Peer, ageMs: 100000);

        Assert.False(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: true));
    }

    [Fact]
    public void NoMoreThanTwoPeersOweTheSameBlockOutsideEndGame()
    {
        // Staleness is measured from the oldest outstanding request, whose age only grows, so
        // without this cap a block past the soft timeout stayed eligible for every fast peer
        // forever - and each extra copy is duplicate data by the time the cancel races it.
        var fixture = new StrategyFixture(softTimeoutMs: 100);
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.OtherPeer, ageMs: 100000);
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.ThirdPeer, ageMs: 100000);

        Assert.False(fixture.Standard.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: true));
    }
}

public class EndGameBlockRequestStrategyTests
{
    [Fact]
    public void ABlockAlreadyHeldIsNotRequestedAgain()
    {
        var fixture = new StrategyFixture();
        var state = new PieceState(1, 4);
        state.Blocks[0] = true;

        Assert.False(fixture.EndGame.IsBlockRequestable(state, 1, 0, fixture.Peer, isPeerFast: true));
    }

    [Fact]
    public void DuplicationNeedsNoStalenessOrSpeedInEndGame()
    {
        // End game exists to finish the last blocks, where waiting out a soft timeout on a slow peer
        // is the delay being avoided. A fresh request from someone else is no obstacle.
        var fixture = new StrategyFixture();
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.OtherPeer, ageMs: 0);

        Assert.True(fixture.EndGame.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: false));
    }

    [Fact]
    public void APeerIsStillNotAskedTwiceForTheSameBlock()
    {
        var fixture = new StrategyFixture();
        fixture.AddOutstandingRequest(piece: 1, offset: 0, fixture.Peer, ageMs: 0);

        Assert.False(fixture.EndGame.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: true));
    }

    [Fact]
    public void FourPeersMayOweTheSameBlockButNotFive()
    {
        var fixture = new StrategyFixture();
        fixture.AddOutstandingRequest(1, 0, fixture.OtherPeer, 0);
        fixture.AddOutstandingRequest(1, 0, fixture.ThirdPeer, 0);
        fixture.AddOutstandingRequest(1, 0, fixture.FourthPeer, 0);

        Assert.True(fixture.EndGame.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: false));

        fixture.AddOutstandingRequest(1, 0, fixture.FifthPeer, 0);
        Assert.False(fixture.EndGame.IsBlockRequestable(new PieceState(1, 4), 1, 0, fixture.Peer, isPeerFast: false));
    }
}

public class TransferProgressReporterTests
{
    [Fact]
    public void TheFirstPieceIsAMilestoneAndAnOrdinaryOneIsNot()
    {
        // The whole job of the reporter is choosing a level: a torrent with tens of thousands of
        // pieces would otherwise write an information line per piece into a consumer's log.
        var (reporter, logger, torrent) = CreateReporter(pieceCount: 1000);

        Complete(torrent, 0);
        reporter.ReportPieceCompleted(0);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information);

        logger.Entries.Clear();
        Complete(torrent, 1);
        reporter.ReportPieceCompleted(1);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
    }

    [Fact]
    public void TheLastPieceIsAlwaysAMilestone()
    {
        var (reporter, logger, torrent) = CreateReporter(pieceCount: 4);
        for (int i = 0; i < 4; i++) Complete(torrent, i);

        reporter.ReportPieceCompleted(3);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void ReportingSurvivesATorrentThatNobodyIsListeningTo()
    {
        // Nothing is required to subscribe, and the reporter runs on every completed piece.
        var (reporter, _, torrent) = CreateReporter(pieceCount: 8);
        Complete(torrent, 0);

        reporter.ReportPieceCompleted(0);
    }

    private static void Complete(Torrent torrent, int pieceIndex) => torrent.Pieces.AddPiece(pieceIndex);

    private static (TransferProgressReporter Reporter, RecordingLogger Logger, Torrent Torrent) CreateReporter(int pieceCount)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384L * pieceCount;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = metadata.Info.FullSize });
        for (int i = 0; i < pieceCount; i++) metadata.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var logger = new RecordingLogger();
        return (new TransferProgressReporter(torrent, logger), logger, torrent);
    }

    private sealed class RecordingLogger : ILogger<TransferProgressReporter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>Shared setup for the two request strategies, which differ only in their rules.</summary>
internal sealed class StrategyFixture
{
    public StrategyFixture(int softTimeoutMs = 3000)
    {
        Clock = new FakeTimeProvider();
        Torrent = TorrentTestUtility.CreateMinimal();
        Tracker = new BlockRequestTracker();

        Peer = NewPeer();
        OtherPeer = NewPeer();
        ThirdPeer = NewPeer();
        FourthPeer = NewPeer();
        FifthPeer = NewPeer();

        Standard = new StandardBlockRequestStrategy(Tracker, Clock, _ => softTimeoutMs, 16384);
        EndGame = new EndGameBlockRequestStrategy(Tracker, 16384);
    }

    public FakeTimeProvider Clock { get; }
    public Torrent Torrent { get; }
    public BlockRequestTracker Tracker { get; }
    public PeerCommunication Peer { get; }
    public PeerCommunication OtherPeer { get; }
    public PeerCommunication ThirdPeer { get; }
    public PeerCommunication FourthPeer { get; }
    public PeerCommunication FifthPeer { get; }
    public StandardBlockRequestStrategy Standard { get; }
    public EndGameBlockRequestStrategy EndGame { get; }

    public void AddOutstandingRequest(int piece, int offset, PeerCommunication peer, int ageMs)
    {
        var request = new BlockRequest
        {
            PieceIndex = piece,
            Offset = offset,
            Length = 16384,
            Timestamp = Clock.GetUtcNow().AddMilliseconds(-ageMs)
        };

        Tracker.GetOrAddPeerRequests(peer)[(piece, offset)] = request;
        Tracker.AddBlockRequest(piece, offset, peer, request);
    }

    private PeerCommunication NewPeer() => new(Torrent, new NullPeerListener(), Clock);
}

internal sealed class NullPeerListener : IPeerListener
{
    public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
    public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
    public Task MessageReceivedAsync(IPeerCommunication peer, PeerSharp.Messages.PeerMessage msg) => Task.CompletedTask;
    public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
    public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
    public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
    public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
    public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
}
