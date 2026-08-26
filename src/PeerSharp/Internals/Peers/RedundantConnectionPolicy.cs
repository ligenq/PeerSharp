namespace PeerSharp.Internals.Peers;

/// <summary>
/// Whether a connection has nothing left to exchange and should be closed.
/// </summary>
/// <remarks>
/// <para>
/// Two seeds have no reason to stay connected. Neither will ever request anything, neither will ever
/// send anything, and the connection occupies a slot on both sides that a peer which actually wants
/// data could be using. The same is true, one-sidedly, of a peer that has everything when we are not
/// interested in it.
/// </para>
/// <para>
/// Left unclosed, such a connection lives until the idle timeout reaps it two minutes later. That was
/// measured: a completed dual transfer held its finished peers open for the full
/// <see cref="ProtocolConstants.IdleTimeoutMs"/>, so a benchmark that timed the whole exchange
/// recorded 805 MB moved in two seconds as 2.2 MB/s over 120 seconds. The wasted slots are the real
/// cost - a seed in a busy swarm accumulates connections to other seeds and stops being able to
/// accept the leechers it exists to serve.
/// </para>
/// <para>
/// This follows libtorrent's <c>disconnect_if_redundant</c>, including its two separate cases and its
/// precondition that both ends have metadata: a peer that is still fetching the info dictionary may
/// want it from us, and appears to have no pieces precisely because it does not know how many there
/// are yet.
/// </para>
/// </remarks>
internal static class RedundantConnectionPolicy
{
    /// <summary>Why a connection was judged redundant, for the log that records the disconnect.</summary>
    public enum Verdict
    {
        /// <summary>The connection is still useful to at least one side.</summary>
        Keep,

        /// <summary>Both ends have everything. Neither can serve the other.</summary>
        BothUploadOnly,

        /// <summary>The peer has everything and we want none of it.</summary>
        UninterestingSeed
    }

    /// <summary>
    /// Judges one connection.
    /// </summary>
    /// <param name="weHaveMetadata">Whether our torrent has its info dictionary.</param>
    /// <param name="peerHasMetadata">
    /// Whether the peer has told us what it holds. A peer still fetching metadata has reported
    /// nothing and must not be read as holding nothing.
    /// </param>
    /// <param name="peerIsUploadOnly">
    /// Whether the peer has every piece, or has said so with BEP 21 <c>upload_only</c>.
    /// </param>
    /// <param name="weAreUploadOnly">Whether everything we intend to fetch is already here.</param>
    /// <param name="weAreInterested">Whether we currently want anything this peer has.</param>
    public static Verdict Judge(
        bool weHaveMetadata,
        bool peerHasMetadata,
        bool peerIsUploadOnly,
        bool weAreUploadOnly,
        bool weAreInterested)
    {
        if (!weHaveMetadata || !peerHasMetadata || !peerIsUploadOnly)
        {
            return Verdict.Keep;
        }

        if (weAreUploadOnly)
        {
            return Verdict.BothUploadOnly;
        }

        // A partial seed still downloading has no use for a peer holding only what it already has.
        // Interest is the engine's own answer to "is there anything here for me", so it decides.
        return weAreInterested ? Verdict.Keep : Verdict.UninterestingSeed;
    }
}
