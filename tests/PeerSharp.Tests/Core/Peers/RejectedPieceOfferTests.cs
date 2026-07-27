using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// What happens to a piece the peer offered and then refused.
///
/// <para>
/// Allowed-fast and suggested pieces are offers: the peer is saying it will serve these. When it then
/// rejects a request for one, it has changed its mind - typically because it ran out of upload slots.
/// Leaving the offer standing invites the identical request again, and the identical rejection after
/// it, for as long as the connection lasts. A live run showed one peer rejecting 1,366 requests while
/// still serving 116 MiB, so this is waste alongside a working transfer rather than a stall, which is
/// exactly the kind of thing that goes unnoticed.
/// </para>
///
/// <para>
/// libtorrent drops the piece from whichever set it came from on this signal - allowed-fast while
/// choked, suggested otherwise - and validates the reject before acting on it, so a peer cannot
/// withdraw offers it never made by naming arbitrary pieces.
/// </para>
/// </summary>
public class RejectedPieceOfferTests
{
    private static PeerCommunication CreatePeer()
    {
        var torrent = TorrentTestUtility.CreateMinimal(new TorrentFileMetadata());
        return new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
    }

    // The add-side is private; the existing PeerCommunication tests reach it the same way.
    private static void Offer(PeerCommunication peer, string methodName, int pieceIndex)
    {
        var method = typeof(PeerCommunication).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        method.Invoke(peer, [pieceIndex]);
    }

    [Fact]
    public void WithdrawingAnAllowedFastPieceStopsItBeingRequestable()
    {
        var peer = CreatePeer();

        Offer(peer, "AddAllowedFastPiece", 7);
        Assert.True(peer.IsAllowedFast(7));
        Assert.Equal(1, peer.AllowedFastCount);

        peer.WithdrawOfferedPiece(7, fromAllowedFast: true);

        Assert.False(peer.IsAllowedFast(7));
        Assert.Equal(0, peer.AllowedFastCount);
    }

    /// <summary>
    /// The cached snapshot must be rebuilt, or callers iterating the set keep seeing the withdrawn
    /// piece and keep requesting it - which is the loop this exists to break.
    /// </summary>
    [Fact]
    public void WithdrawalIsVisibleThroughTheCachedSnapshot()
    {
        var peer = CreatePeer();

        Offer(peer, "AddAllowedFastPiece", 3);
        Offer(peer, "AddAllowedFastPiece", 9);

        // Materialise the snapshot so a stale cache would be caught.
        Assert.Equal(2, peer.GetAllowedFastPieces().Count);

        peer.WithdrawOfferedPiece(3, fromAllowedFast: true);

        var remaining = peer.GetAllowedFastPieces();
        Assert.Single(remaining);
        Assert.Equal(9, remaining[0]);
    }

    [Fact]
    public void WithdrawingASuggestedPieceLeavesAllowedFastAlone()
    {
        var peer = CreatePeer();

        Offer(peer, "AddAllowedFastPiece", 4);
        Offer(peer, "AddSuggestedPiece", 4);

        // Same index in both sets: withdrawing from one must not touch the other, or a reject while
        // unchoked would silently revoke a piece we are still entitled to request while choked.
        peer.WithdrawOfferedPiece(4, fromAllowedFast: false);

        Assert.True(peer.IsAllowedFast(4));
        Assert.DoesNotContain(4, peer.GetSuggestedPieces());
    }

    [Fact]
    public void WithdrawingAPieceThatWasNeverOfferedIsHarmless()
    {
        var peer = CreatePeer();

        Offer(peer, "AddAllowedFastPiece", 2);

        peer.WithdrawOfferedPiece(99, fromAllowedFast: true);

        Assert.True(peer.IsAllowedFast(2));
        Assert.Equal(1, peer.AllowedFastCount);
    }
}
