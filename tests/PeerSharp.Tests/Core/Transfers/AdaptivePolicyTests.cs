using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Config;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using PeerSharp.Internals.Transfers;
using PeerSharp.PiecePicking;
using System.Net;

namespace PeerSharp.Tests.Core.Transfers;

/// <summary>
/// The policies that used to be constants: how many pieces stay open, and how long a block request
/// is given before it is duplicated or abandoned.
/// </summary>
public class AdaptivePolicyTests
{
    [Theory]
    // A byte budget divided by the piece size is the same exposure at either end of the range. The
    // old flat 32 meant 8 MiB on the first and 512 MiB on the last.
    [InlineData(256 * 1024, 64)]        // 16 MiB budget / 256 KiB = 64, under the demand cap
    [InlineData(4 * 1024 * 1024, 4)]    // 16 MiB / 4 MiB = 4
    [InlineData(16 * 1024 * 1024, 1)]   // 16 MiB / 16 MiB = 1 piece, the same 16 MiB of exposure
    public void ActivePieces_FollowTheByteBudgetDividedByPieceSize(int pieceSize, int expected)
    {
        var (transfer, torrent) = CreateTransfer(pieceSize, willingPeers: 40);
        torrent.Settings.Transfer.ActivePieceByteBudget = 16L * 1024 * 1024;
        torrent.Settings.Transfer.MinActivePieces = 1; // the floor has its own test

        Assert.Equal(expected, ActivePieceCapacity(transfer));
    }

    [Fact]
    public void ActivePieces_KeepTheFloorEvenWhenOnePieceExceedsTheWholeBudget()
    {
        // A 16 MiB piece against a 16 MiB budget works out to one piece. Endgame needs more than one
        // piece to have anywhere to go, so the floor wins and the exposure is knowingly exceeded.
        var (transfer, torrent) = CreateTransfer(pieceSize: 16 * 1024 * 1024, willingPeers: 40);
        torrent.Settings.Transfer.ActivePieceByteBudget = 16L * 1024 * 1024;

        Assert.Equal(torrent.Settings.Transfer.MinActivePieces, ActivePieceCapacity(transfer));
    }

    [Fact]
    public void ActivePieces_AreAlsoCappedByThePeersWillingToSend()
    {
        // Opening pieces nobody will serve only scatters requests over more partial pieces.
        var (transfer, torrent) = CreateTransfer(pieceSize: 256 * 1024, willingPeers: 5);
        torrent.Settings.Transfer.ActivePieceByteBudget = 1024L * 1024 * 1024;
        torrent.Settings.Transfer.ActivePiecesPerPeer = 2;
        torrent.Settings.Transfer.MinActivePieces = 1;

        Assert.Equal(10, ActivePieceCapacity(transfer));
    }

    [Fact]
    public void ActivePieces_NeverFallBelowTheFloorOrRiseAboveTheCeiling()
    {
        var (transfer, torrent) = CreateTransfer(pieceSize: 256 * 1024, willingPeers: 0);
        var settings = torrent.Settings.Transfer;

        // No willing peers at all: demand is zero, but endgame still needs somewhere to go.
        settings.MinActivePieces = 6;
        Assert.Equal(6, ActivePieceCapacity(transfer));

        // Budget and demand both far above the ceiling.
        var (wide, wideTorrent) = CreateTransfer(pieceSize: 16 * 1024, willingPeers: 500);
        wideTorrent.Settings.Transfer.ActivePieceByteBudget = 4L * 1024 * 1024 * 1024;
        wideTorrent.Settings.Transfer.MaxActivePieces = 100;
        Assert.Equal(100, ActivePieceCapacity(wide));
    }

    [Fact]
    public void ActivePieces_TrackSettingsChangedWhileRunning()
    {
        var (transfer, torrent) = CreateTransfer(pieceSize: 1024 * 1024, willingPeers: 40);
        var settings = torrent.Settings.Transfer;

        settings.ActivePieceByteBudget = 32L * 1024 * 1024;
        Assert.Equal(32, ActivePieceCapacity(transfer));

        settings.ActivePieceByteBudget = 64L * 1024 * 1024;
        Assert.Equal(64, ActivePieceCapacity(transfer));
    }

    [Fact]
    public void BlockTimeouts_SeparateASteadyPeerFromAJitteryOneWithTheSameMean()
    {
        // The case a multiple of the mean alone cannot express. Both peers average about 200ms; one
        // answers in a steady 200, the other swings between 50 and 2000.
        var (transfer, torrent) = CreateTransfer(pieceSize: 256 * 1024, willingPeers: 0);
        torrent.Settings.Transfer.BlockTimeoutVarianceMultiplier = 4;
        torrent.Settings.Transfer.MinBlockTimeoutMs = 1;
        torrent.Settings.Transfer.MaxBlockTimeoutMs = 600000;

        var steady = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        for (int i = 0; i < 40; i++) steady.RecordRtt(200);

        var jittery = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        for (int i = 0; i < 40; i++) jittery.RecordRtt(i % 2 == 0 ? 50 : 2000);

        int steadyTimeout = SoftTimeout(transfer, steady);
        int jitteryTimeout = SoftTimeout(transfer, jittery);

        Assert.True(steady.RttVarianceMs < jittery.RttVarianceMs,
            $"steady variance {steady.RttVarianceMs} should be below jittery {jittery.RttVarianceMs}");
        Assert.True(jitteryTimeout > steadyTimeout,
            $"jittery peer got {jitteryTimeout}ms, steady got {steadyTimeout}ms");
    }

    [Fact]
    public void BlockTimeouts_UseTheConfiguredMultipliersAndBounds()
    {
        var (transfer, torrent) = CreateTransfer(pieceSize: 256 * 1024, willingPeers: 0);
        var settings = torrent.Settings.Transfer;
        settings.BlockTimeoutVarianceMultiplier = 0; // mean only, so the arithmetic is exact
        settings.MinBlockTimeoutMs = 1;
        settings.MaxBlockTimeoutMs = 600000;
        settings.MinBlockHardTimeoutMs = 1;
        settings.MaxBlockHardTimeoutMs = 600000;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        for (int i = 0; i < 40; i++) peer.RecordRtt(200);
        int rtt = peer.SmoothedRttMs;

        settings.BlockTimeoutRttMultiplier = 6;
        settings.BlockHardTimeoutRttMultiplier = 10;
        Assert.Equal(rtt * 6, SoftTimeout(transfer, peer));
        Assert.Equal(rtt * 10, HardTimeout(transfer, peer));

        settings.BlockTimeoutRttMultiplier = 3;
        Assert.Equal(rtt * 3, SoftTimeout(transfer, peer));

        // The ceiling is what the old fixed bounds were, and it is now the caller's to set.
        settings.MaxBlockTimeoutMs = 100;
        Assert.Equal(100, SoftTimeout(transfer, peer));

        settings.MinBlockTimeoutMs = 5000;
        settings.MaxBlockTimeoutMs = 15000;
        Assert.Equal(5000, SoftTimeout(transfer, peer));
    }

    [Fact]
    public void BlockTimeouts_FallBackToTheFloorWhenThePeerHasNoRoundTripYet()
    {
        var (transfer, torrent) = CreateTransfer(pieceSize: 256 * 1024, willingPeers: 0);
        torrent.Settings.Transfer.MinBlockTimeoutMs = 2500;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        typeof(PeerCommunication).GetField("_smoothedRttMs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(peer, 0);

        Assert.Equal(2500, SoftTimeout(transfer, peer));
    }

    private static int ActivePieceCapacity(FileTransfer transfer) =>
        (int)typeof(FileTransfer)
            .GetMethod("CalculateMaxActivePieces", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(transfer, null)!;

    private static int SoftTimeout(FileTransfer transfer, PeerCommunication peer) =>
        InvokeTimeout(transfer, "GetAdaptiveSoftTimeout", peer);

    private static int HardTimeout(FileTransfer transfer, PeerCommunication peer) =>
        InvokeTimeout(transfer, "GetAdaptiveHardTimeout", peer);

    private static int InvokeTimeout(FileTransfer transfer, string name, PeerCommunication peer) =>
        (int)typeof(FileTransfer)
            .GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(transfer, [peer])!;

    private static (FileTransfer Transfer, Torrent Torrent) CreateTransfer(int pieceSize, int willingPeers)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = (uint)pieceSize;
        metadata.Info.FullSize = (long)pieceSize * 64;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = metadata.Info.FullSize });
        for (int i = 0; i < 64; i++) metadata.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var transfer = torrent.FileTransferInternal;
        Assert.NotNull(transfer);

        for (int i = 0; i < willingPeers; i++)
        {
            var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System)
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse($"10.0.{i / 256}.{i % 256}"), 6881)
            };
            peer.SetPeerChokingForTesting(false);
            ((PeerManager)torrent.PeersInternal).AddConnectedPeerForTesting(peer);
        }

        return ((FileTransfer)transfer, torrent);
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
