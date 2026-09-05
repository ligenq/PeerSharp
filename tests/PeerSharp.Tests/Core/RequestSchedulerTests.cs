using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;
using PeerSharp.PiecePicking;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core;

public class RequestSchedulerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    public async Task Scheduling_RespectsRemoteCapacity(int advertised, int expected)
    {
        var fixture = CreateSchedulerFixture(8, 1, 8, 6);
        typeof(PeerCommunication).GetProperty(nameof(PeerCommunication.RemoteExtensions))!
            .SetValue(fixture.Peer, new ExtensionHandshake { RequestQueueDepth = advertised });

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, false, () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(expected, requests.Count);
    }

    [Fact]
    public async Task Scheduling_ReReadsLocalAndRemoteLimits_WithoutDiscardingInFlightRequests()
    {
        var fixture = CreateSchedulerFixture(16, 1, 16, 6);
        fixture.Torrent.Settings.Transfer.MaxRequestsPerPeer = 2;
        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, false, () => false);
        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(2, requests.Count);

        fixture.Torrent.Settings.Transfer.MaxRequestsPerPeer = 10;
        var handshake = new ExtensionHandshake { RequestQueueDepth = 4 };
        typeof(PeerCommunication).GetProperty(nameof(PeerCommunication.RemoteExtensions))!.SetValue(fixture.Peer, handshake);
        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, false, () => false);
        Assert.Equal(4, requests.Count);
        handshake.RequestQueueDepth = 1;
        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, false, () => false);
        Assert.Equal(4, requests.Count);
        handshake.RequestQueueDepth = 8;
        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, false, () => false);
        Assert.Equal(8, requests.Count);
    }

    [Fact]
    public void Pipeline_CanExceedDefaultCeiling_AndScalesPenaltiesWithConfiguredLimit()
    {
        var fixture = CreateSchedulerFixture(1, 1, 1, 1000);
        fixture.Torrent.Settings.Transfer.MaxRequestsPerPeer = 1000;
        fixture.Torrent.Settings.Transfer.EstimatedBandwidthBytesPerSec = 100_000_000;
        Assert.Equal(1000, fixture.Peer.GetAdaptivePipelineDepth());
        fixture.Peer.IncrementStrikes();
        Assert.Equal(900, fixture.Peer.GetAdaptivePipelineDepth());
        fixture.Torrent.Settings.Transfer.MaxRequestsPerPeer = 1;
        Assert.Equal(1, fixture.Peer.GetAdaptivePipelineDepth());
    }

    [Theory]
    [InlineData(0, 10, 1, 0)]
    [InlineData(8, 0, 1, 0)]
    [InlineData(8, 10, 1, 8)]
    [InlineData(8, 10, 2, 4)]
    [InlineData(8, 3, 1, 3)]
    [InlineData(8, 10, 0, 8)]
    public void RequestQueuePolicy_CalculatesNewPieceLimit_FromQueueDeficit(
        int remainingRequestSlots,
        int activePieceSlotsAvailable,
        int blocksPerPiece,
        int expected)
    {
        int actual = RequestQueuePolicy.CalculateNewPieceStartLimit(remainingRequestSlots, activePieceSlotsAvailable, blocksPerPiece);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_SendsRequests_ForActivePiece()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384 * 2;
        metadata.Info.FullSize = 16384 * 2;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var pieceStateManager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, maxActivePieces: 1);
        var requestTracker = new BlockRequestTracker();

        var scheduler = new RequestScheduler(new RequestSchedulerOptions
        {
            Torrent = torrent,
            RequestTracker = requestTracker,
            PieceStateManager = pieceStateManager,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger<RequestScheduler>.Instance,
            BlockSize = 16384,
            GetSoftTimeoutMs = _ => 3000
        }, piecePicker);

        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_peerChoking", 0);
        SetPrivateField(peer, "_connected", 1);
        peer.PeerPieces.AddPiece(0);

        var state = new PieceState(0, 2);
        pieceStateManager.TryAddPiece(state);

        await scheduler.EvaluateNextRequestsAsync(peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(requestTracker.TryGetPeerRequests(peer, out var requests));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_StartsEnoughNewPieces_ToFillConfiguredSingleBlockQueue()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 8, blocksPerPiece: 1, maxActivePieces: 8, maxRequestsPerPeer: 6);

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(6, requests.Count);
        Assert.Equal(6, fixture.PieceStateManager.Count);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_StartsEnoughMultiBlockPieces_ToFillQueue()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 8, blocksPerPiece: 4, maxActivePieces: 8, maxRequestsPerPeer: 6);

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(6, requests.Count);
        Assert.Equal(2, fixture.PieceStateManager.Count);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_AccountsForExistingPendingRequests_WhenStartingNewPieces()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 8, blocksPerPiece: 1, maxActivePieces: 8, maxRequestsPerPeer: 6);
        AddPendingRequest(fixture.RequestTracker, fixture.Peer, -1, 0);
        AddPendingRequest(fixture.RequestTracker, fixture.Peer, -1, 16384);

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(6, requests.Count);
        Assert.Equal(4, fixture.PieceStateManager.Count);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_RespectsActivePieceCapacity_WhenQueueWantsMorePieces()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 8, blocksPerPiece: 1, maxActivePieces: 3, maxRequestsPerPeer: 6);

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(3, requests.Count);
        Assert.Equal(3, fixture.PieceStateManager.Count);
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_FillsActivePieces_BeforeStartingNewPieces()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 8, blocksPerPiece: 4, maxActivePieces: 8, maxRequestsPerPeer: 6);
        fixture.PieceStateManager.TryAddPiece(new PieceState(0, 4));

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out var requests));
        Assert.Equal(6, requests.Count);
        Assert.Equal(2, fixture.PieceStateManager.Count);
        Assert.Equal(4, requests.Keys.Count(k => k.Piece == 0));
    }

    [Fact]
    public async Task EvaluateNextRequestsAsync_DisconnectedPeer_DoesNotReclaimFailedPiece()
    {
        var fixture = CreateSchedulerFixture(pieceCount: 1, blocksPerPiece: 1, maxActivePieces: 1, maxRequestsPerPeer: 1);
        var state = new PieceState(0, 1);
        state.RecordHashFailure();
        Assert.True(fixture.PieceStateManager.TryAddPiece(state));
        SetPrivateField(fixture.Peer, "_connected", 0);

        await fixture.Scheduler.EvaluateNextRequestsAsync(fixture.Peer, endGameMode: false, isQueueFull: () => false);

        var replacement = new PeerCommunication(fixture.Torrent, new MockPeerListener(), TimeProvider.System);
        Assert.True(state.TryClaimForRetry(replacement, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30)));
        Assert.False(fixture.RequestTracker.TryGetPeerRequests(fixture.Peer, out _));
    }

    private static void SetPrivateField(object target, string fieldName, int value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static SchedulerFixture CreateSchedulerFixture(int pieceCount, int blocksPerPiece, int maxActivePieces, int maxRequestsPerPeer)
    {
        const int blockSize = 16384;
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = (uint)(blockSize * blocksPerPiece);
        metadata.Info.FullSize = metadata.Info.PieceSize * pieceCount;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var pickerContext = new SchedulerPiecePickerContext { PieceCount = pieceCount };
        torrent.Settings.Transfer.MaxRequestsPerPeer = maxRequestsPerPeer;
        var piecePicker = new PiecePicker(pickerContext, TimeProvider.System, new Random(0));
        var pieceStateManager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, maxActivePieces);
        pickerContext.PieceStateManager = pieceStateManager;
        var requestTracker = new BlockRequestTracker();

        var scheduler = new RequestScheduler(new RequestSchedulerOptions
        {
            Torrent = torrent,
            RequestTracker = requestTracker,
            PieceStateManager = pieceStateManager,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger<RequestScheduler>.Instance,
            BlockSize = blockSize,
            GetSoftTimeoutMs = _ => 3000
        }, piecePicker);

        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_peerChoking", 0);
        SetPrivateField(peer, "_connected", 1);
        for (int i = 0; i < pieceCount; i++)
        {
            peer.PeerPieces.AddPiece(i);
        }

        return new SchedulerFixture(torrent, scheduler, pieceStateManager, requestTracker, peer);
    }

    private static void AddPendingRequest(BlockRequestTracker requestTracker, PeerCommunication peer, int pieceIndex, int offset)
    {
        requestTracker.AddBlockRequest(pieceIndex, offset, peer, new BlockRequest
        {
            PieceIndex = pieceIndex,
            Offset = offset,
            Length = 16384,
            Timestamp = TimeProvider.System.GetUtcNow(),
            Attempts = 1
        });
    }

    private sealed record SchedulerFixture(
        Torrent Torrent,
        RequestScheduler Scheduler,
        PieceStateManager PieceStateManager,
        BlockRequestTracker RequestTracker,
        PeerCommunication Peer);

    private sealed class SchedulerPiecePickerContext : IPiecePickerContext
    {
        public PieceStateManager? PieceStateManager { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;
        public int PieceCount { get; set; }
        public int CompletedPieceCount { get; set; }
        public IReadOnlyList<int>? StreamingPriorityPieces => null;

        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot() => null;

        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection) => Priority.Normal;

        public bool HasPiece(int pieceIndex) => false;

        public bool IsPieceActive(int pieceIndex) => PieceStateManager?.ContainsPiece(pieceIndex) == true;

        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection) => true;
    }

    private sealed class MockPeerListener : IPeerListener
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
