using PeerSharp.Core;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// The public BEP 46 publisher identity. Most of what matters here is that the private material is
/// handled correctly: a read-only identity must refuse to sign, a persisted seed must round-trip to
/// the same public key, and the magnet link it emits must be one this library can parse back.
/// </summary>
public class TorrentPublisherKeyTests
{
    [Fact]
    public void Create_ProducesAPublishableIdentity()
    {
        var key = TorrentPublisherKey.Create();

        Assert.True(key.CanPublish);
        Assert.Equal(TorrentPublisherKey.PublicKeyLength, key.PublicKey.Length);
        Assert.Equal(TorrentPublisherKey.SeedLength, key.Seed.Length);
    }

    [Fact]
    public void Create_ProducesADistinctIdentityEachTime()
    {
        Assert.NotEqual(
            TorrentPublisherKey.Create().PublicKey.ToArray(),
            TorrentPublisherKey.Create().PublicKey.ToArray());
    }

    /// <summary>
    /// The persistence contract: storing the seed and restoring it must give back the same
    /// identity, or a publisher loses the ability to update their own torrent across restarts.
    /// </summary>
    [Fact]
    public void FromSeed_RestoresTheSameIdentity()
    {
        var original = TorrentPublisherKey.Create();

        var restored = TorrentPublisherKey.FromSeed(original.Seed.Span);

        Assert.Equal(original.PublicKey.ToArray(), restored.PublicKey.ToArray());
        Assert.Equal(original.ToMagnetLink(), restored.ToMagnetLink());
        Assert.True(restored.CanPublish);
    }

    [Fact]
    public void FromPublicKey_CannotPublish()
    {
        var publisher = TorrentPublisherKey.Create();

        var follower = TorrentPublisherKey.FromPublicKey(publisher.PublicKey.Span);

        Assert.False(follower.CanPublish);
        Assert.True(follower.Seed.IsEmpty);
        Assert.Equal(publisher.PublicKey.ToArray(), follower.PublicKey.ToArray());
    }

    /// <summary>
    /// A read-only identity must fail loudly rather than produce a signature that verifies against
    /// nothing.
    /// </summary>
    [Fact]
    public void FromPublicKey_RefusesToSign()
    {
        var follower = TorrentPublisherKey.FromPublicKey(TorrentPublisherKey.Create().PublicKey.Span);

        Assert.Throws<InvalidOperationException>(
            () => DhtItemCodec.CreateSigned(follower, [], 0, Bep46Resolver.BuildRecord(InfoHash.CreateRandom())));
    }

    /// <summary>
    /// The expanded form is what libtorrent's key generation yields, so an existing key store has
    /// to work. Verified against BEP 44's own key pair rather than a round trip of our own making.
    /// </summary>
    [Fact]
    public void FromExpandedKey_MatchesTheSpecKeyPair()
    {
        var expanded = Convert.FromHexString(
            "e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d" +
            "b7e7a9fea2c0eb269d61e3b38e450a22e754941ac78479d6c54e1faf6037881d");

        var key = TorrentPublisherKey.FromExpandedKey(expanded);

        Assert.True(key.CanPublish);
        Assert.Equal(
            "77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548",
            Convert.ToHexStringLower(key.PublicKey.ToArray()));

        // The seed cannot be recovered from an expanded key, so it is reported empty rather than
        // fabricated - the caller must persist the expanded bytes they supplied.
        Assert.True(key.Seed.IsEmpty);
    }

    [Fact]
    public void FromExpandedKey_SignsCompatiblyWithTheSpecVector()
    {
        var expanded = Convert.FromHexString(
            "e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d" +
            "b7e7a9fea2c0eb269d61e3b38e450a22e754941ac78479d6c54e1faf6037881d");
        var key = TorrentPublisherKey.FromExpandedKey(expanded);

        var value = new BEncoding.BString("Hello World!"u8.ToArray());
        var item = DhtItemCodec.CreateSigned(key, [], 1, value);

        Assert.Equal(
            "305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff" +
            "1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01",
            Convert.ToHexStringLower(item.Signature));
    }

    /// <summary>
    /// The link a publisher hands out must be one this library accepts, hex salt included. This is
    /// the round trip that would have caught the UTF-8 salt bug on its own.
    /// </summary>
    [Fact]
    public void ToMagnetLink_RoundTripsThroughMagnetLinkParse()
    {
        var key = TorrentPublisherKey.Create();

        var parsed = MagnetLink.Parse(key.ToMagnetLink());

        Assert.True(parsed.IsSelfUpdating);
        Assert.Equal(key.PublicKey.ToArray(), parsed.PublicKey.ToArray());
        Assert.True(parsed.Salt.IsEmpty);
    }

    [Fact]
    public void ToMagnetLink_WithSalt_RoundTripsThroughMagnetLinkParse()
    {
        var key = TorrentPublisherKey.Create();
        var salt = "nightly"u8.ToArray();

        var parsed = MagnetLink.Parse(key.ToMagnetLink(salt));

        Assert.True(parsed.IsSelfUpdating);
        Assert.Equal(key.PublicKey.ToArray(), parsed.PublicKey.ToArray());
        Assert.Equal(salt, parsed.Salt.ToArray());
    }

    [Fact]
    public void ToMagnetLink_RefusesAnOversizedSalt()
    {
        var key = TorrentPublisherKey.Create();

        Assert.Throws<ArgumentException>(() => key.ToMagnetLink(new byte[65]));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void FromSeed_RejectsAWrongLength(int length)
    {
        Assert.Throws<ArgumentException>(() => TorrentPublisherKey.FromSeed(new byte[length]));
    }

    [Fact]
    public void FromExpandedKey_RejectsAWrongLength()
    {
        Assert.Throws<ArgumentException>(() => TorrentPublisherKey.FromExpandedKey(new byte[63]));
    }

    [Fact]
    public void FromPublicKey_RejectsAWrongLength()
    {
        Assert.Throws<ArgumentException>(() => TorrentPublisherKey.FromPublicKey(new byte[31]));
    }

    /// <summary>
    /// The identity's address must agree with the BEP 44 derivation the DHT layer uses, or a
    /// publisher would write where no subscriber reads.
    /// </summary>
    [Fact]
    public void PublicKey_AddressesTheSameTargetAsTheDhtLayer()
    {
        var key = TorrentPublisherKey.Create();
        var salt = "feed"u8.ToArray();

        Assert.Equal(
            DhtItemCodec.ComputeMutableTarget(key.PublicKey.Span, salt),
            Bep46Resolver.ComputeTarget(key.PublicKey.Span, salt));

        // And the seed-derived key agrees with Ed25519 directly.
        Assert.Equal(Ed25519.PublicKeyFromSeed(key.Seed.Span), key.PublicKey.ToArray());
    }
}
