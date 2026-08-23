using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// When a failed dial earns an immediate second one instead of waiting out the backoff.
///
/// <para>
/// This exists because the encryption alternation had an unstated precondition. A failed handshake
/// flips what this client offers a peer next time, which only helps if there is a next time - and
/// dialling is driven entirely by peer supply, so a peer offered once and never announced again never
/// got one. Against a libtorrent built without encryption, such a peer was unreachable rather than
/// slow: one attempt, and the flipped preference recorded and never used.
/// </para>
/// </summary>
public class FastReconnectPolicyTests
{
    [Fact]
    public void APeerThatHungUpMidEncryptionHandshakeIsRetriedAtOnce()
    {
        // The one failure that says nothing about the peer's encryption support, so the flipped offer
        // is genuinely new information rather than the same dial again.
        Assert.True(FastReconnectPolicy.ShouldRetryImmediately(
            hungUpDuringEncryptionHandshake: true,
            fastReconnects: 0));
    }

    [Fact]
    public void EveryOtherFailureWaitsOutTheBackoff()
    {
        // A refusal, a timeout or an unreachable host all mean the same thing on an immediate redial,
        // and an earlier measurement against a live swarm put that at 72 failures in 77 attempts.
        Assert.False(FastReconnectPolicy.ShouldRetryImmediately(
            hungUpDuringEncryptionHandshake: false,
            fastReconnects: 0));
    }

    [Fact]
    public void TheGrantIsBoundedPerPeer()
    {
        // libtorrent stops honouring its rewind past the second time. A peer that answers neither
        // choice costs two extra dials, not an unbounded stream of them - which is the failure mode
        // this whole area has been burnt by before.
        Assert.True(FastReconnectPolicy.ShouldRetryImmediately(true, fastReconnects: 0));
        Assert.True(FastReconnectPolicy.ShouldRetryImmediately(true, fastReconnects: 1));

        Assert.False(FastReconnectPolicy.ShouldRetryImmediately(true, fastReconnects: 2));
        Assert.False(FastReconnectPolicy.ShouldRetryImmediately(true, fastReconnects: 50));
    }

    [Fact]
    public void TheBoundIsTwo()
    {
        // Stated rather than implied, because the number is borrowed and a drifting copy of someone
        // else's constant is worse than no copy.
        Assert.Equal(2, FastReconnectPolicy.MaxFastReconnects);
    }
}
