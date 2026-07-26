using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// The official test vectors from BEP 44's "Test vectors" section.
///
/// This is the strongest interoperability evidence available without a live network. Every value
/// below - public key, expanded private key, target IDs, signatures - comes from the
/// specification, produced by a different implementation. Reproducing the signatures byte for byte
/// means the signature buffer layout, the target derivation and the Ed25519 arithmetic all agree
/// with the reference; getting any one of them subtly wrong would change the output.
///
/// The private keys are given as 64-byte expanded keys (clamped scalar followed by nonce prefix),
/// not seeds, which is why <see cref="Ed25519.SignWithExpandedKey"/> exists.
/// </summary>
public class Bep44SpecVectorTests
{
    /// <summary>The value used by all three vectors: the bencoded string "Hello World!".</summary>
    private static BString Value => new(Encoding.ASCII.GetBytes("Hello World!"));

    private const string PublicKeyHex =
        "77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548";

    private const string ExpandedPrivateKeyHex =
        "e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d" +
        "b7e7a9fea2c0eb269d61e3b38e450a22e754941ac78479d6c54e1faf6037881d";

    /// <summary>
    /// Sanity check on the fixture itself: the expanded key must actually belong to the public key
    /// the spec pairs it with, or every other assertion here would be testing the wrong thing.
    /// </summary>
    [Fact]
    public void ExpandedPrivateKey_DerivesTheSpecPublicKey()
    {
        var derived = Ed25519.PublicKeyFromExpandedKey(Convert.FromHexString(ExpandedPrivateKeyHex));

        Assert.Equal(PublicKeyHex, Convert.ToHexStringLower(derived));
    }

    // ---- Test 1: mutable, no salt ------------------------------------------------------------

    [Fact]
    public void Test1_SignatureBufferMatchesTheSpec()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer([], sequenceNumber: 1, Value);

        Assert.Equal("3:seqi1e1:v12:Hello World!", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void Test1_TargetIdMatchesTheSpec()
    {
        var target = DhtItemCodec.ComputeMutableTarget(Convert.FromHexString(PublicKeyHex), []);

        Assert.Equal("4a533d47ec9c7d95b1ad75f576cffc641853b750", target.ToString());
    }

    /// <summary>
    /// Produces the spec's signature from the spec's key. Ed25519 is deterministic, so this is an
    /// exact-equality check against another implementation's output rather than a round trip.
    /// </summary>
    [Fact]
    public void Test1_SignatureMatchesTheSpec()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer([], 1, Value);

        var signature = Ed25519.SignWithExpandedKey(buffer, Convert.FromHexString(ExpandedPrivateKeyHex));

        Assert.Equal(
            "305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff" +
            "1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01",
            Convert.ToHexStringLower(signature));
    }

    /// <summary>
    /// The other direction: a signature produced elsewhere must verify here. This is what a
    /// storage node does with every incoming put from a foreign client.
    /// </summary>
    [Fact]
    public void Test1_SpecSignatureVerifies()
    {
        var item = new DhtMutableItem
        {
            Value = Value,
            PublicKey = Convert.FromHexString(PublicKeyHex),
            SequenceNumber = 1,
            Signature = Convert.FromHexString(
                "305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff" +
                "1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01"),
        };

        Assert.True(item.VerifySignature());
        Assert.Equal(DhtPutError.None, DhtItemCodec.Validate(item));
        Assert.Equal("4a533d47ec9c7d95b1ad75f576cffc641853b750", item.Target.ToString());
    }

    // ---- Test 2: mutable with salt "foobar" --------------------------------------------------

    [Fact]
    public void Test2_SignatureBufferMatchesTheSpec()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer("foobar"u8, sequenceNumber: 1, Value);

        Assert.Equal("4:salt6:foobar3:seqi1e1:v12:Hello World!", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void Test2_TargetIdMatchesTheSpec()
    {
        var target = DhtItemCodec.ComputeMutableTarget(Convert.FromHexString(PublicKeyHex), "foobar"u8);

        Assert.Equal("411eba73b6f087ca51a3795d9c8c938d365e32c1", target.ToString());
    }

    [Fact]
    public void Test2_SignatureMatchesTheSpec()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer("foobar"u8, 1, Value);

        var signature = Ed25519.SignWithExpandedKey(buffer, Convert.FromHexString(ExpandedPrivateKeyHex));

        Assert.Equal(
            "6834284b6b24c3204eb2fea824d82f88883a3d95e8b4a21b8c0ded553d17d17d" +
            "df9a8a7104b1258f30bed3787e6cb896fca78c58f8e03b5f18f14951a87d9a08",
            Convert.ToHexStringLower(signature));
    }

    [Fact]
    public void Test2_SpecSignatureVerifies()
    {
        var item = new DhtMutableItem
        {
            Value = Value,
            PublicKey = Convert.FromHexString(PublicKeyHex),
            SequenceNumber = 1,
            Salt = "foobar"u8.ToArray(),
            Signature = Convert.FromHexString(
                "6834284b6b24c3204eb2fea824d82f88883a3d95e8b4a21b8c0ded553d17d17d" +
                "df9a8a7104b1258f30bed3787e6cb896fca78c58f8e03b5f18f14951a87d9a08"),
        };

        Assert.True(item.VerifySignature());
        Assert.Equal("411eba73b6f087ca51a3795d9c8c938d365e32c1", item.Target.ToString());
    }

    /// <summary>
    /// Tests 1 and 2 differ only by the salt, and their signatures and targets both differ. That
    /// is the pair that proves the salt is genuinely covered by both derivations rather than being
    /// quietly dropped somewhere.
    /// </summary>
    [Fact]
    public void Test1AndTest2_DifferOnlyBySaltAndProduceDifferentResults()
    {
        var unsalted = DhtItemCodec.ComputeMutableTarget(Convert.FromHexString(PublicKeyHex), []);
        var salted = DhtItemCodec.ComputeMutableTarget(Convert.FromHexString(PublicKeyHex), "foobar"u8);

        Assert.NotEqual(unsalted, salted);

        var key = Convert.FromHexString(ExpandedPrivateKeyHex);
        Assert.NotEqual(
            Ed25519.SignWithExpandedKey(DhtItemCodec.BuildSignatureBuffer([], 1, Value), key),
            Ed25519.SignWithExpandedKey(DhtItemCodec.BuildSignatureBuffer("foobar"u8, 1, Value), key));
    }

    // ---- Test 3: immutable -------------------------------------------------------------------

    [Fact]
    public void Test3_ImmutableTargetIdMatchesTheSpec()
    {
        var item = new DhtImmutableItem { Value = Value };

        Assert.Equal("e5f96f6f38320f0f33959cb4d3d656452117aadb", item.Target.ToString());
        Assert.Equal(DhtPutError.None, DhtItemCodec.Validate(item));
    }

    // ---- Documented limits and error codes ---------------------------------------------------

    /// <summary>
    /// The wire values of the error codes are interoperability surface in their own right: a peer
    /// reads the number, not our enum.
    /// </summary>
    [Fact]
    public void ErrorCodes_MatchTheSpecNumbers()
    {
        Assert.Equal(203, (int)DhtPutError.Protocol);
        Assert.Equal(205, (int)DhtPutError.ValueTooBig);
        Assert.Equal(206, (int)DhtPutError.InvalidSignature);
        Assert.Equal(207, (int)DhtPutError.SaltTooBig);
        Assert.Equal(301, (int)DhtPutError.CasMismatch);
        Assert.Equal(302, (int)DhtPutError.SequenceNumberTooLow);
    }

    [Fact]
    public void Limits_MatchTheSpec()
    {
        Assert.Equal(1000, DhtItem.MaxValueLength);
        Assert.Equal(64, DhtItem.MaxSaltLength);
    }
}
