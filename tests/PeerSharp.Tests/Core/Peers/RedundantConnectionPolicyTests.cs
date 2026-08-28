using PeerSharp.Internals.Peers;
using Verdict = PeerSharp.Internals.Peers.RedundantConnectionPolicy.Verdict;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Closing connections that neither side can use.
///
/// <para>
/// Two seeds have nothing to exchange, but PeerSharp kept them connected until the two-minute idle
/// timeout noticed. That was found by benchmarking: a dual transfer moved 805 MB in two seconds and
/// then held its finished peers open for the rest of the two minutes, so the measurement recorded
/// 2.2 MB/s. The number was an artifact; the wasted connection slots are not, and a seed in a busy
/// swarm fills up with other seeds it cannot serve.
/// </para>
/// </summary>
public class RedundantConnectionPolicyTests
{
    [Fact]
    public void TwoSeedsHaveNothingToExchange()
    {
        Assert.Equal(Verdict.BothUploadOnly, RedundantConnectionPolicy.Judge(
            weHaveMetadata: true,
            peerHasMetadata: true,
            peerIsUploadOnly: true,
            weAreUploadOnly: true,
            weAreInterested: false));
    }

    [Fact]
    public void ASeedWeWantNothingFromIsAlsoRedundant()
    {
        // We are still downloading, but not from this peer - it holds only pieces we already have.
        Assert.Equal(Verdict.UninterestingSeed, RedundantConnectionPolicy.Judge(
            weHaveMetadata: true,
            peerHasMetadata: true,
            peerIsUploadOnly: true,
            weAreUploadOnly: false,
            weAreInterested: false));
    }

    [Fact]
    public void ASeedWeStillWantSomethingFromIsKept()
    {
        Assert.Equal(Verdict.Keep, RedundantConnectionPolicy.Judge(
            weHaveMetadata: true,
            peerHasMetadata: true,
            peerIsUploadOnly: true,
            weAreUploadOnly: false,
            weAreInterested: true));
    }

    [Fact]
    public void ALeecherIsKeptEvenWhenWeAreASeed()
    {
        // The entire point of seeding. This is the case a careless rule would break.
        Assert.Equal(Verdict.Keep, RedundantConnectionPolicy.Judge(
            weHaveMetadata: true,
            peerHasMetadata: true,
            peerIsUploadOnly: false,
            weAreUploadOnly: true,
            weAreInterested: false));
    }

    [Fact]
    public void APeerStillFetchingMetadataIsNeverRedundant()
    {
        // It has reported no pieces because it does not yet know how many there are, and it may want
        // the info dictionary from us. Reading that silence as "holds everything" would disconnect
        // exactly the peer we are able to help.
        Assert.Equal(Verdict.Keep, RedundantConnectionPolicy.Judge(
            weHaveMetadata: true,
            peerHasMetadata: false,
            peerIsUploadOnly: true,
            weAreUploadOnly: true,
            weAreInterested: false));
    }

    [Fact]
    public void WeKeepEveryoneWhileWeStillLackMetadata()
    {
        // A magnet has no pieces yet, so "we want nothing" would otherwise be true of every peer.
        Assert.Equal(Verdict.Keep, RedundantConnectionPolicy.Judge(
            weHaveMetadata: false,
            peerHasMetadata: true,
            peerIsUploadOnly: true,
            weAreUploadOnly: true,
            weAreInterested: false));
    }
}
