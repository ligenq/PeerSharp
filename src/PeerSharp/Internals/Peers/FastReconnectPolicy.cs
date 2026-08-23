namespace PeerSharp.Internals.Peers;

/// <summary>
/// Whether a failed outgoing attempt has earned an immediate second one with the other encryption
/// choice, rather than waiting out the usual backoff.
/// </summary>
/// <remarks>
/// <para>
/// Encryption support cannot be discovered without trying, so a failed handshake flips what this
/// client offers a peer next time. That only helps if there is a next time, and dialling is driven
/// entirely by peer supply - a tracker announce, the DHT, PEX, or a caller handing over an address. A
/// swarm re-offers its peers constantly, so the next attempt arrives on its own. A peer offered once
/// and never announced again gets none, and the flipped preference is recorded and never used, which
/// is the whole of a LAN machine or a seedbox added by hand.
/// </para>
/// <para>
/// libtorrent has the same alternation and closes the same hole with <c>fast_reconnect</c>: beginning
/// an encrypted handshake rewinds that peer's reconnect clock, so a failure leaves it eligible at
/// once instead of after the backoff, and its session tick redials from the peer list. PeerSharp has
/// no such tick, so the retry is queued at the point of failure instead.
/// </para>
/// </remarks>
internal static class FastReconnectPolicy
{
    /// <summary>
    /// How many times the backoff may be waived for one peer, matching libtorrent's own bound: it
    /// stops honouring the rewind past the second time, so a peer answering neither choice costs two
    /// extra dials rather than an unbounded stream of them.
    /// </summary>
    public const int MaxFastReconnects = 2;

    /// <summary>
    /// Decides whether to retry a peer straight away.
    /// </summary>
    /// <param name="hungUpDuringEncryptionHandshake">
    /// Whether the attempt died with the peer closing the connection part-way through the MSE
    /// handshake. This is the only failure worth another dial immediately: the peer said nothing
    /// about encryption either way, so the flipped offer is genuinely new information. Every other
    /// failure - a refusal, a timeout, an unreachable host - is left to the ordinary backoff, because
    /// dialling straight back meets the same reason again.
    /// </param>
    /// <param name="fastReconnects">How many times this peer has already been granted one.</param>
    public static bool ShouldRetryImmediately(bool hungUpDuringEncryptionHandshake, int fastReconnects)
    {
        return hungUpDuringEncryptionHandshake && fastReconnects < MaxFastReconnects;
    }
}
