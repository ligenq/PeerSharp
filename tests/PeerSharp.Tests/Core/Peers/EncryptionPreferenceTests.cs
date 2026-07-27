using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Choosing between an encrypted and a plaintext handshake for an outgoing connection.
///
/// <para>
/// Whether a peer will accept encryption cannot be discovered without trying, and a peer that hangs up
/// on one form may accept the other. PeerSharp used to resolve that inside a single attempt: on the
/// peer closing mid-handshake it concluded "doesn't support encryption", reconnected, and retried in
/// plaintext. Measured against a live swarm that retry failed 72 times out of 77, and one peer was
/// redialled fifteen times - because peers hang up for reasons that have nothing to do with encryption
/// (connection limits, not having the torrent, already being connected to us), and dialling straight
/// back meets the same reason again. One peer in that run was diagnosed as not supporting encryption
/// and then completed an encrypted handshake with us minutes later, which settles it.
/// </para>
///
/// <para>
/// Both reference implementations decide this per peer rather than per attempt. libtorrent flips a
/// <c>pe_support</c> flag on the peer before dialling and flips it back when the handshake completes;
/// Transmission does not retry at all and marks a peer that sent nothing back as unconnectable. These
/// pin the libtorrent behaviour.
/// </para>
/// </summary>
public class EncryptionPreferenceTests
{
    private static PeerHistory NewHistory() =>
        new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881) };

    [Fact]
    public void EncryptionIsOfferedToAPeerWeKnowNothingAbout()
    {
        Assert.True(NewHistory().OfferEncryptionNext);
    }

    [Fact]
    public void AFailedHandshakeSwitchesWhatIsOfferedNextTime()
    {
        var history = NewHistory();

        history.RegisterHandshakeFailure();
        Assert.False(history.OfferEncryptionNext);

        // Still failing, so keep alternating rather than settling on the one that just failed.
        history.RegisterHandshakeFailure();
        Assert.True(history.OfferEncryptionNext);
    }

    [Fact]
    public void SuccessSettlesOnWhateverActuallyWorked()
    {
        var history = NewHistory();

        history.RegisterHandshakeFailure();
        Assert.False(history.OfferEncryptionNext);

        // Plaintext got through, so keep using it rather than alternating back.
        history.RegisterHandshakeSuccess(wasEncrypted: false);
        Assert.False(history.OfferEncryptionNext);

        history.RegisterHandshakeSuccess(wasEncrypted: false);
        Assert.False(history.OfferEncryptionNext);
    }

    [Fact]
    public void APeerThatAcceptsEncryptionKeepsBeingOfferedIt()
    {
        var history = NewHistory();

        history.RegisterHandshakeSuccess(wasEncrypted: true);
        Assert.True(history.OfferEncryptionNext);

        history.RegisterHandshakeSuccess(wasEncrypted: true);
        Assert.True(history.OfferEncryptionNext);
    }

    /// <summary>
    /// A peer that only speaks one of the two is reached on the following attempt, which is the whole
    /// point of alternating rather than giving up.
    /// </summary>
    [Fact]
    public void APlaintextOnlyPeerIsReachedOnTheSecondAttempt()
    {
        var history = NewHistory();

        Assert.True(history.OfferEncryptionNext);   // first attempt: encrypted, refused
        history.RegisterHandshakeFailure();

        Assert.False(history.OfferEncryptionNext);  // second attempt: plaintext, accepted
        history.RegisterHandshakeSuccess(wasEncrypted: false);

        Assert.False(history.OfferEncryptionNext);  // and it stays there
    }

    [Fact]
    public void SuccessClearsTheFailureRun()
    {
        var history = NewHistory();

        history.RegisterHandshakeFailure();
        history.RegisterHandshakeFailure();
        Assert.Equal(2, history.HandshakeFailureCount);

        history.RegisterHandshakeSuccess(wasEncrypted: true);
        Assert.Equal(0, history.HandshakeFailureCount);
    }
}
