using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 44 item encoding. Almost everything here is interoperability surface: a signature buffer
/// that is off by one character, or a target derived slightly differently, produces records that
/// round-trip perfectly against this implementation and against no other. So the tests assert
/// exact bytes and derive expected values independently rather than through the code under test.
/// </summary>
public class DhtItemCodecTests
{
    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// The worked example from BEP 44. This single assertion is what pins the whole signature
    /// format - the concatenation is not a bencoded dictionary and has no enclosing d/e.
    /// </summary>
    [Fact]
    public void BuildSignatureBuffer_MatchesTheWorkedExampleFromTheBep()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer(
            "foobar"u8,
            sequenceNumber: 1,
            Text("Hello World!"));

        Assert.Equal("4:salt6:foobar3:seqi1e1:v12:Hello World!", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void BuildSignatureBuffer_OmitsTheSaltPairWhenThereIsNoSalt()
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer([], sequenceNumber: 1, Text("Hello World!"));

        Assert.Equal("3:seqi1e1:v12:Hello World!", Encoding.ASCII.GetString(buffer));
    }

    [Theory]
    [InlineData(0, "3:seqi0e1:v3:abc")]
    [InlineData(1, "3:seqi1e1:v3:abc")]
    [InlineData(42, "3:seqi42e1:v3:abc")]
    [InlineData(1234567890123, "3:seqi1234567890123e1:v3:abc")]
    public void BuildSignatureBuffer_EncodesSequenceNumbersAsBencodedIntegers(long sequenceNumber, string expected)
    {
        var buffer = DhtItemCodec.BuildSignatureBuffer([], sequenceNumber, Text("abc"));

        Assert.Equal(expected, Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public void BuildSignatureBuffer_CarriesTheBencodedValueVerbatim()
    {
        // A dictionary value, to prove v is embedded as bencode rather than re-encoded somehow.
        var value = new BDict();
        value.Dict["ih"] = new BString(Convert.FromHexString("0102030405060708090a0b0c0d0e0f1011121314"));

        var buffer = DhtItemCodec.BuildSignatureBuffer([], sequenceNumber: 7, value);
        var expected = "3:seqi7e1:v"u8.ToArray().Concat(BencodeWriter.Write(value)).ToArray();

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void ComputeImmutableTarget_IsTheSha1OfTheBencodedValue()
    {
        var value = Text("Hello World!");

        var expected = SHA1.HashData(BencodeWriter.Write(value));

        Assert.Equal(expected, DhtItemCodec.ComputeImmutableTarget(value).Span.ToArray());
    }

    [Fact]
    public void ComputeMutableTarget_IsTheSha1OfKeyThenSalt()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());
        var salt = "photos"u8.ToArray();

        var expected = SHA1.HashData([.. publicKey, .. salt]);

        Assert.Equal(expected, DhtItemCodec.ComputeMutableTarget(publicKey, salt).Span.ToArray());
    }

    [Fact]
    public void ComputeMutableTarget_WithoutSalt_IsTheSha1OfTheKeyAlone()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());

        Assert.Equal(SHA1.HashData(publicKey), DhtItemCodec.ComputeMutableTarget(publicKey, []).Span.ToArray());
    }

    /// <summary>
    /// The salt is what lets one key own several records. If it did not change the target, a
    /// publisher could not keep a profile and a photo list under the same identity.
    /// </summary>
    [Fact]
    public void ComputeMutableTarget_DiffersPerSalt()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());

        var none = DhtItemCodec.ComputeMutableTarget(publicKey, []);
        var photos = DhtItemCodec.ComputeMutableTarget(publicKey, "photos"u8);
        var profile = DhtItemCodec.ComputeMutableTarget(publicKey, "profile"u8);

        Assert.NotEqual(none, photos);
        Assert.NotEqual(none, profile);
        Assert.NotEqual(photos, profile);
    }

    [Fact]
    public void CreateSigned_ProducesAVerifiableItem()
    {
        var seed = Ed25519.GenerateSeed();

        var item = DhtItemCodec.CreateSigned(seed, "salty"u8, sequenceNumber: 3, Text("payload"));

        Assert.True(item.VerifySignature());
        Assert.Equal(Ed25519.PublicKeyFromSeed(seed), item.PublicKey);
        Assert.Equal(3, item.SequenceNumber);
        Assert.Equal("salty"u8.ToArray(), item.Salt);
        Assert.Equal(DhtPutError.None, DhtItemCodec.Validate(item));
    }

    [Fact]
    public void CreateSigned_WithoutSalt_LeavesSaltNull()
    {
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], sequenceNumber: 0, Text("x"));

        Assert.Null(item.Salt);
        Assert.True(item.VerifySignature());
    }

    /// <summary>
    /// Each signed field must actually be covered. If any of them were left out of the buffer,
    /// an attacker could alter it while keeping the signature valid.
    /// </summary>
    [Fact]
    public void VerifySignature_RejectsTamperingWithAnySignedField()
    {
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, "salt"u8, sequenceNumber: 5, Text("original"));

        Assert.False((item with { Value = Text("modified") }).VerifySignature());
        Assert.False((item with { SequenceNumber = 6 }).VerifySignature());
        Assert.False((item with { Salt = "different"u8.ToArray() }).VerifySignature());
        Assert.False((item with { Salt = null }).VerifySignature());
        Assert.False((item with { PublicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed()) }).VerifySignature());
    }

    /// <summary>
    /// Built by hand rather than through CreateSigned, which refuses to sign an oversized value.
    /// A storage node still has to reject one that arrives off the wire.
    /// </summary>
    [Fact]
    public void Validate_RejectsAnOversizedValue()
    {
        var seed = Ed25519.GenerateSeed();
        // The bencoded form of a 1000-byte string is "1000:" plus the bytes, so 1005 total.
        var oversized = new BString(new byte[1000]);

        var item = new DhtMutableItem
        {
            Value = oversized,
            PublicKey = Ed25519.PublicKeyFromSeed(seed),
            SequenceNumber = 0,
            Signature = Ed25519.Sign(DhtItemCodec.BuildSignatureBuffer([], 0, oversized), seed),
        };

        // The signature is genuinely valid; it is the size rule that must reject it.
        Assert.True(item.VerifySignature());
        Assert.Equal(DhtPutError.ValueTooBig, DhtItemCodec.Validate(item));
    }

    [Fact]
    public void CreateSigned_RefusesToSignAnOversizedValue()
    {
        var seed = Ed25519.GenerateSeed();

        Assert.Throws<ArgumentException>(() => DhtItemCodec.CreateSigned(seed, [], 0, new BString(new byte[1001])));
    }

    [Fact]
    public void CreateSigned_RefusesAnOversizedSalt()
    {
        var seed = Ed25519.GenerateSeed();

        Assert.Throws<ArgumentException>(() => DhtItemCodec.CreateSigned(seed, new byte[65], 0, Text("x")));
    }

    [Fact]
    public void Validate_RejectsAnOversizedSalt()
    {
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), new byte[64], 0, Text("x"));
        var oversized = item with { Salt = new byte[65] };

        Assert.Equal(DhtPutError.SaltTooBig, DhtItemCodec.Validate(oversized));
    }

    [Fact]
    public void Validate_RejectsAForgedSignature()
    {
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("payload"));
        var forged = item with { Signature = RandomNumberGenerator.GetBytes(64) };

        Assert.Equal(DhtPutError.InvalidSignature, DhtItemCodec.Validate(forged));
    }

    [Fact]
    public void Validate_RejectsMalformedKeyOrSignatureLengths()
    {
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("payload"));

        Assert.Equal(DhtPutError.Protocol, DhtItemCodec.Validate(item with { PublicKey = new byte[31] }));
        Assert.Equal(DhtPutError.Protocol, DhtItemCodec.Validate(item with { Signature = new byte[63] }));
        Assert.Equal(DhtPutError.Protocol, DhtItemCodec.Validate(item with { SequenceNumber = -1 }));
    }

    [Fact]
    public void Validate_AcceptsAnImmutableItem()
    {
        var item = new DhtImmutableItem { Value = Text("immutable payload") };

        Assert.Equal(DhtPutError.None, DhtItemCodec.Validate(item));
        Assert.Equal(DhtItemCodec.ComputeImmutableTarget(item.Value), item.Target);
    }

    // ---- Replacement rules -------------------------------------------------------------------

    [Fact]
    public void CheckReplacement_AcceptsIntoAnEmptyAddress()
    {
        var incoming = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 0, Text("first"));

        Assert.Equal(DhtPutError.None, DhtItemCodec.CheckReplacement(stored: null, incoming, compareAndSwap: null));
    }

    [Fact]
    public void CheckReplacement_AcceptsANewerSequenceNumber()
    {
        var seed = Ed25519.GenerateSeed();
        var stored = DhtItemCodec.CreateSigned(seed, [], 4, Text("old"));
        var incoming = DhtItemCodec.CreateSigned(seed, [], 5, Text("new"));

        Assert.Equal(DhtPutError.None, DhtItemCodec.CheckReplacement(stored, incoming, compareAndSwap: null));
    }

    /// <summary>
    /// Replaying an older record over a newer one is the attack this rule exists to stop - it
    /// would let anyone who captured an old signed value roll a publisher back to it.
    /// </summary>
    [Fact]
    public void CheckReplacement_RejectsAnOlderSequenceNumber()
    {
        var seed = Ed25519.GenerateSeed();
        var stored = DhtItemCodec.CreateSigned(seed, [], 5, Text("current"));
        var replay = DhtItemCodec.CreateSigned(seed, [], 4, Text("stale"));

        Assert.Equal(DhtPutError.SequenceNumberTooLow, DhtItemCodec.CheckReplacement(stored, replay, null));
    }

    [Fact]
    public void CheckReplacement_AcceptsAnIdenticalRepublishAtTheSameSequence()
    {
        var seed = Ed25519.GenerateSeed();
        var stored = DhtItemCodec.CreateSigned(seed, [], 5, Text("same"));
        var republish = DhtItemCodec.CreateSigned(seed, [], 5, Text("same"));

        Assert.Equal(DhtPutError.None, DhtItemCodec.CheckReplacement(stored, republish, null));
    }

    [Fact]
    public void CheckReplacement_RejectsADifferentValueAtTheSameSequence()
    {
        var seed = Ed25519.GenerateSeed();
        var stored = DhtItemCodec.CreateSigned(seed, [], 5, Text("original"));
        var fork = DhtItemCodec.CreateSigned(seed, [], 5, Text("forked"));

        Assert.Equal(DhtPutError.SequenceNumberTooLow, DhtItemCodec.CheckReplacement(stored, fork, null));
    }

    [Fact]
    public void CheckReplacement_HonoursCompareAndSwap()
    {
        var seed = Ed25519.GenerateSeed();
        var stored = DhtItemCodec.CreateSigned(seed, [], 5, Text("current"));
        var incoming = DhtItemCodec.CreateSigned(seed, [], 6, Text("next"));

        Assert.Equal(DhtPutError.None, DhtItemCodec.CheckReplacement(stored, incoming, compareAndSwap: 5));
        Assert.Equal(DhtPutError.CasMismatch, DhtItemCodec.CheckReplacement(stored, incoming, compareAndSwap: 4));
    }

    [Fact]
    public void CheckReplacement_FailsCompareAndSwapAgainstAnAbsentItem()
    {
        var incoming = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("value"));

        Assert.Equal(DhtPutError.CasMismatch, DhtItemCodec.CheckReplacement(stored: null, incoming, compareAndSwap: 0));
    }

    [Fact]
    public void Target_IsStableAcrossRecomputation()
    {
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), "s"u8, 1, Text("v"));

        Assert.Equal(item.Target, item.Target);
        Assert.Equal(DhtItemCodec.ComputeMutableTarget(item.PublicKey, item.Salt), item.Target);
    }
}
