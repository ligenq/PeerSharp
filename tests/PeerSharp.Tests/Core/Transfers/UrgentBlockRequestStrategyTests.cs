using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Transfers;

public class UrgentBlockRequestStrategyTests
{
    private const int BlockSize = 16384;

    private readonly BlockRequestTracker _tracker = new();
    private readonly FakeTimeProvider _time = new();
    private readonly UrgentBlockRequestStrategy _strategy;
    private readonly Torrent _torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());

    public UrgentBlockRequestStrategyTests()
    {
        _strategy = new UrgentBlockRequestStrategy(_tracker, _time, BlockSize);
    }

    [Fact]
    public void ABlockNobodyOwes_IsAskedOfAnyPeer_ButNotOnceItHasArrived()
    {
        Assert.True(_strategy.IsBlockRequestable(new PieceState(0, 1), 0, 0, Peer(), isPeerFast: false));

        var arrived = new PieceState(0, 1);
        arrived.Blocks[0] = true;
        Assert.False(_strategy.IsBlockRequestable(arrived, 0, 0, Peer(), isPeerFast: true));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, true)]
    public void ABlockAskedOfFourPeers_IsAskedOfAnother_OnlyOnceTheyHaveKeptItWaiting(int secondsLater, bool askedAgain)
    {
        for (int i = 0; i < UrgentBlockRequestStrategy.MaxFreshRequestsPerBlock; i++)
        {
            Owe(Peer());
        }

        _time.Advance(TimeSpan.FromSeconds(secondsLater));

        Assert.Equal(askedAgain, _strategy.IsBlockRequestable(new PieceState(0, 1), 0, 0, Peer(), isPeerFast: true));
    }

    [Fact]
    public void ABlockOwedAlready_IsNotAskedOfASlowPeer_NorAgainOfThePeerOwingIt()
    {
        var owing = Peer();
        Owe(owing);

        Assert.False(_strategy.IsBlockRequestable(new PieceState(0, 1), 0, 0, Peer(), isPeerFast: false));
        Assert.False(_strategy.IsBlockRequestable(new PieceState(0, 1), 0, 0, owing, isPeerFast: true));
        Assert.True(_strategy.IsBlockRequestable(new PieceState(0, 1), 0, 0, Peer(), isPeerFast: true));
    }

    private void Owe(PeerCommunication peer) =>
        _tracker.AddBlockRequest(0, 0, peer, new BlockRequest { PieceIndex = 0, Offset = 0, Length = BlockSize, Timestamp = _time.GetUtcNow(), Attempts = 1 });

    private PeerCommunication Peer() => new(_torrent, new SilentListener(), TimeProvider.System);

    private sealed class SilentListener : IPeerListener
    {
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}
