using PeerSharp.Internals;

namespace PeerSharp.Tests.Core.Transfers;

public class MerkleHashRequestCoordinatorTests
{
    [Fact]
    public void SelectBep30Peer_ReturnsFirstEligiblePeer()
    {
        var peers = new[] { new Candidate("a", false), new Candidate("b", true), new Candidate("c", true) };

        var selection = MerkleHashRequestCoordinator.SelectBep30Peer(peers, peer => peer.CanRequest);

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, selection.Status);
        Assert.Equal("b", selection.Peer!.Name);
        Assert.Null(selection.RequestKey);
    }

    [Fact]
    public void SelectBep30Peer_WhenNoPeerCanRequest_ReturnsNoPeer()
    {
        var peers = new[] { new Candidate("a", false), new Candidate("b", false) };

        var selection = MerkleHashRequestCoordinator.SelectBep30Peer(peers, peer => peer.CanRequest);

        Assert.Equal(MerkleHashRequestSelectionStatus.NoPeer, selection.Status);
        Assert.Null(selection.Peer);
    }

    [Fact]
    public void SelectV2Peer_WhenRequestIsNull_ReturnsNoRequest()
    {
        var coordinator = new MerkleHashRequestCoordinator(TimeSpan.FromSeconds(3));

        var selection = coordinator.SelectV2Peer<Candidate>(
            request: null,
            peers: new[] { new Candidate("a", true) },
            canRequest: peer => peer.CanRequest,
            now: DateTimeOffset.UnixEpoch);

        Assert.Equal(MerkleHashRequestSelectionStatus.NoRequest, selection.Status);
        Assert.Null(selection.Peer);
    }

    [Fact]
    public void SelectV2Peer_ReservesRequestAndThrottlesDuplicateInsideRetryWindow()
    {
        var coordinator = new MerkleHashRequestCoordinator(TimeSpan.FromSeconds(3));
        var request = Request(index: 10);
        var peers = new[] { new Candidate("a", true) };
        var now = DateTimeOffset.UnixEpoch;

        var first = coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now);
        var second = coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now.AddSeconds(1));

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, first.Status);
        Assert.Equal("a", first.Peer!.Name);
        Assert.Equal("010203|10", first.RequestKey);
        Assert.Equal(MerkleHashRequestSelectionStatus.Throttled, second.Status);
        Assert.Equal(first.RequestKey, second.RequestKey);
        Assert.Null(second.Peer);
    }

    [Fact]
    public void SelectV2Peer_AllowsRetryAfterInterval()
    {
        var coordinator = new MerkleHashRequestCoordinator(TimeSpan.FromSeconds(3));
        var request = Request(index: 10);
        var peers = new[] { new Candidate("a", true) };
        var now = DateTimeOffset.UnixEpoch;

        coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now);
        var retry = coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now.AddSeconds(4));

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, retry.Status);
        Assert.Equal("a", retry.Peer!.Name);
    }

    [Fact]
    public void CompleteFailedV2Request_RemovesThrottle()
    {
        var coordinator = new MerkleHashRequestCoordinator(TimeSpan.FromSeconds(3));
        var request = Request(index: 10);
        var peers = new[] { new Candidate("a", true) };
        var now = DateTimeOffset.UnixEpoch;

        var first = coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now);
        coordinator.CompleteFailedV2Request(first.RequestKey!);
        var second = coordinator.SelectV2Peer(request, peers, peer => peer.CanRequest, now.AddSeconds(1));

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, second.Status);
        Assert.Equal("a", second.Peer!.Name);
    }

    [Fact]
    public void SelectV2Peer_WhenNoPeerCanRequest_DoesNotReserveRequest()
    {
        var coordinator = new MerkleHashRequestCoordinator(TimeSpan.FromSeconds(3));
        var request = Request(index: 10);
        var now = DateTimeOffset.UnixEpoch;

        var noPeer = coordinator.SelectV2Peer(request, new[] { new Candidate("a", false) }, peer => peer.CanRequest, now);
        var selected = coordinator.SelectV2Peer(request, new[] { new Candidate("b", true) }, peer => peer.CanRequest, now.AddSeconds(1));

        Assert.Equal(MerkleHashRequestSelectionStatus.NoPeer, noPeer.Status);
        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, selected.Status);
        Assert.Equal("b", selected.Peer!.Name);
    }

    private static V2HashRequest Request(int index)
    {
        return new V2HashRequest(new byte[] { 1, 2, 3 }, BaseLayer: 2, Index: index, Length: 4, ProofLayers: 5);
    }

    private sealed record Candidate(string Name, bool CanRequest);
}
