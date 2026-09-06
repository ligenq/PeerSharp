using PeerSharp.BEncoding;
using PeerSharp.Core;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtTargetTests
{
    private static readonly byte[] TwentyBytes = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];

    [Fact]
    public void ATargetIsExactlyTwentyBytes()
    {
        Assert.Equal(20, DhtTarget.Length);
        Assert.Equal(TwentyBytes, new DhtTarget(TwentyBytes).Span.ToArray());

        Assert.Throws<ArgumentException>(() => new DhtTarget(new byte[19]));
        Assert.Throws<ArgumentException>(() => new DhtTarget(new byte[21]));
        Assert.Throws<ArgumentException>(() => new DhtTarget([]));
    }

    [Fact]
    public void TheConstructorCopiesRatherThanAliasingTheCallersBuffer()
    {
        // Targets are used as dictionary keys, so a caller reusing its buffer must not be able to
        // change one after the fact.
        var buffer = (byte[])TwentyBytes.Clone();
        var target = new DhtTarget(buffer);

        buffer[0] = 0xFF;

        Assert.Equal(TwentyBytes, target.Span.ToArray());
    }

    [Fact]
    public void ADefaultTargetIsEmptyRatherThanNullReferencing()
    {
        // Structs can always be default-constructed, so every accessor has to survive it.
        DhtTarget target = default;

        Assert.True(target.IsEmpty);
        Assert.Equal(0, target.Span.Length);
        Assert.Equal(0, target.Memory.Length);
        Assert.Equal(string.Empty, target.ToString());
        Assert.Equal(0, target.GetHashCode());
    }

    [Fact]
    public void AConstructedTargetIsNotEmptyAndSpanAndMemoryAgree()
    {
        var target = new DhtTarget(TwentyBytes);

        Assert.False(target.IsEmpty);
        Assert.Equal(target.Span.ToArray(), target.Memory.ToArray());
    }

    [Fact]
    public void FromHex_RoundTripsThroughToString()
    {
        const string hex = "0102030405060708090a0b0c0d0e0f1011121314";
        var target = DhtTarget.FromHex(hex);

        Assert.Equal(TwentyBytes, target.Span.ToArray());
        Assert.Equal(hex, target.ToString());
    }

    [Fact]
    public void FromHex_RejectsNullAndMalformedInput()
    {
        Assert.Throws<ArgumentNullException>(() => DhtTarget.FromHex(null!));
        Assert.Throws<FormatException>(() => DhtTarget.FromHex("zz"));
        Assert.Throws<ArgumentException>(() => DhtTarget.FromHex("0102"));
    }

    [Fact]
    public void EqualityComparesContentRatherThanIdentity()
    {
        var first = new DhtTarget(TwentyBytes);
        var same = new DhtTarget((byte[])TwentyBytes.Clone());
        var different = new DhtTarget(new byte[20]);

        Assert.True(first.Equals(same));
        Assert.True(first == same);
        Assert.False(first != same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());

        Assert.False(first.Equals(different));
        Assert.True(first != different);
    }

    [Fact]
    public void EqualsObject_IsFalseForAnythingElse()
    {
        var target = new DhtTarget(TwentyBytes);

        Assert.False(target.Equals(null));
        Assert.False(target.Equals("not a target"));
        Assert.True(target.Equals((object)new DhtTarget(TwentyBytes)));
    }

    [Fact]
    public void TwoDefaultTargetsAreEqual()
    {
        Assert.Equal(default(DhtTarget), default(DhtTarget));
        Assert.NotEqual(default, new DhtTarget(TwentyBytes));
    }

    [Fact]
    public void ATargetWorksAsADictionaryKey()
    {
        var map = new Dictionary<DhtTarget, string>
        {
            [new DhtTarget(TwentyBytes)] = "value"
        };

        Assert.Equal("value", map[new DhtTarget((byte[])TwentyBytes.Clone())]);
    }
}

public class DhtItemTests
{
    [Fact]
    public void TheProtocolLimitsAreTheOnesBep44States()
    {
        Assert.Equal(1000, DhtItem.MaxValueLength);
        Assert.Equal(64, DhtItem.MaxSaltLength);
    }

    [Fact]
    public void AnItemCarriesTheValueItWasGiven()
    {
        var value = new BString("hello"u8.ToArray());
        DhtItem item = new DhtImmutableItem { Value = value };

        Assert.Same(value, item.Value);
    }
}

public class DhtImmutableItemTests
{
    [Fact]
    public void TheTargetIsTheHashOfTheValueSoTheSameValueLandsAtTheSameAddress()
    {
        // The defining property of an immutable item: its address is derived from its contents, so
        // two publishers with the same value cannot disagree about where it lives.
        var first = new DhtImmutableItem { Value = new BString("hello"u8.ToArray()) };
        var same = new DhtImmutableItem { Value = new BString("hello"u8.ToArray()) };
        var other = new DhtImmutableItem { Value = new BString("goodbye"u8.ToArray()) };

        Assert.Equal(first.Target, same.Target);
        Assert.NotEqual(first.Target, other.Target);
        Assert.False(first.Target.IsEmpty);
    }

    [Fact]
    public void TheTargetIsComputedOnceAndStaysStable()
    {
        var item = new DhtImmutableItem { Value = new BNumber(42) };

        Assert.Equal(item.Target, item.Target);
        Assert.Equal(DhtItemCodec.ComputeImmutableTarget(item.Value), item.Target);
    }
}

public class DhtMutableItemTests
{
    [Fact]
    public void TheTargetFollowsTheKeyAndSaltRatherThanTheValue()
    {
        // This is what makes an update an update: the address survives a change of contents, so a
        // subscriber holding the key keeps reading the same slot.
        var seed = new byte[32];
        var first = DhtItemCodec.CreateSigned(seed, [], 1, new BString("first"u8.ToArray()));
        var second = DhtItemCodec.CreateSigned(seed, [], 2, new BString("second"u8.ToArray()));

        Assert.Equal(first.Target, second.Target);

        var salted = DhtItemCodec.CreateSigned(seed, "salt"u8, 1, new BString("first"u8.ToArray()));
        Assert.NotEqual(first.Target, salted.Target);
    }

    [Fact]
    public void VerifySignature_AcceptsWhatTheCodecSignedAndRejectsATamperedValue()
    {
        var seed = new byte[32];
        seed[0] = 7;
        var item = DhtItemCodec.CreateSigned(seed, [], 5, new BString("payload"u8.ToArray()));

        Assert.True(item.VerifySignature());

        // The signature covers salt, sequence number and value, so changing any of them must break
        // it - otherwise an intermediary could rewrite a record in flight.
        Assert.False((item with { Value = new BString("tampered"u8.ToArray()) }).VerifySignature());
        Assert.False((item with { SequenceNumber = 6 }).VerifySignature());
        Assert.False((item with { Salt = "salt"u8.ToArray() }).VerifySignature());
    }

    [Fact]
    public void VerifySignature_RejectsASignatureFromADifferentKey()
    {
        var mine = DhtItemCodec.CreateSigned(new byte[32], [], 1, new BString("v"u8.ToArray()));
        var theirSeed = new byte[32];
        theirSeed[0] = 1;
        var theirs = DhtItemCodec.CreateSigned(theirSeed, [], 1, new BString("v"u8.ToArray()));

        Assert.False((mine with { Signature = theirs.Signature }).VerifySignature());
    }

    [Fact]
    public void TheSignedFieldsAreCarriedThrough()
    {
        var item = DhtItemCodec.CreateSigned(new byte[32], "s"u8, 9, new BNumber(1));

        Assert.Equal(32, item.PublicKey.Length);
        Assert.Equal(64, item.Signature.Length);
        Assert.Equal(9, item.SequenceNumber);
        Assert.Equal("s"u8.ToArray(), item.Salt);
    }
}

public class DhtExternalIpVoteResultTests
{
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.5");

    [Fact]
    public void Ignored_CarriesNoAddressAndNoVotes()
    {
        var result = DhtExternalIpVoteResult.Ignored;

        Assert.Equal(DhtExternalIpVoteStatus.Ignored, result.Status);
        Assert.Null(result.Address);
        Assert.Equal(0, result.Votes);
        Assert.Equal(0, result.RequiredVotes);
    }

    [Theory]
    [InlineData(nameof(DhtExternalIpVoteStatus.FirstReport))]
    [InlineData(nameof(DhtExternalIpVoteStatus.Changed))]
    [InlineData(nameof(DhtExternalIpVoteStatus.Progress))]
    [InlineData(nameof(DhtExternalIpVoteStatus.Confirmed))]
    [InlineData(nameof(DhtExternalIpVoteStatus.AlreadyConfirmed))]
    public void EachFactoryStampsItsOwnStatusAndKeepsTheTally(string statusName)
    {
        // DhtManager switches on Status and regenerates the node id only on Confirmed, so a factory
        // stamping the wrong one would either miss the regeneration or do it repeatedly.
        var result = statusName switch
        {
            nameof(DhtExternalIpVoteStatus.FirstReport) => DhtExternalIpVoteResult.FirstReport(Address, 1, 5),
            nameof(DhtExternalIpVoteStatus.Changed) => DhtExternalIpVoteResult.Changed(Address, 1, 5),
            nameof(DhtExternalIpVoteStatus.Progress) => DhtExternalIpVoteResult.Progress(Address, 1, 5),
            nameof(DhtExternalIpVoteStatus.Confirmed) => DhtExternalIpVoteResult.Confirmed(Address, 1, 5),
            _ => DhtExternalIpVoteResult.AlreadyConfirmed(Address, 1, 5)
        };

        Assert.Equal(Enum.Parse<DhtExternalIpVoteStatus>(statusName), result.Status);
        Assert.Same(Address, result.Address);
        Assert.Equal(1, result.Votes);
        Assert.Equal(5, result.RequiredVotes);
    }

    [Fact]
    public void ResultsWithTheSameContentsAreEqual()
    {
        Assert.Equal(
            DhtExternalIpVoteResult.Confirmed(Address, 3, 5),
            DhtExternalIpVoteResult.Confirmed(Address, 3, 5));

        Assert.NotEqual(
            DhtExternalIpVoteResult.Confirmed(Address, 3, 5),
            DhtExternalIpVoteResult.Progress(Address, 3, 5));
    }
}

public class Bep46ResolverTests
{
    [Fact]
    public void ComputeTarget_AgreesWithTheMutableItemAddressForTheSameKeyAndSalt()
    {
        // A subscriber computes the address from the key alone; the publisher's item computes it
        // from the same inputs. If these ever disagreed, a published record would be unreachable.
        var item = DhtItemCodec.CreateSigned(new byte[32], "salt"u8, 1, new BNumber(1));

        Assert.Equal(item.Target, Bep46Resolver.ComputeTarget(item.PublicKey, "salt"u8));
    }

    [Fact]
    public void ComputeTarget_SeparatesSaltsUnderOneKey()
    {
        var key = DhtItemCodec.CreateSigned(new byte[32], [], 1, new BNumber(1)).PublicKey;

        var none = Bep46Resolver.ComputeTarget(key, []);
        var salted = Bep46Resolver.ComputeTarget(key, "a"u8);
        var otherSalt = Bep46Resolver.ComputeTarget(key, "b"u8);

        Assert.NotEqual(none, salted);
        Assert.NotEqual(salted, otherSalt);
    }

    [Fact]
    public void BuildRecord_CarriesTheInfoHashUnderTheKeyBep46Names()
    {
        var infoHash = InfoHash.CreateRandom();

        var record = Bep46Resolver.BuildRecord(infoHash);

        Assert.Equal(infoHash.Span.ToArray(), record.GetBytes("ih")!.Value.ToArray());
    }

    [Fact]
    public void BuildRecord_RefusesAV2InfoHash()
    {
        // A BEP 46 record carries a 20-byte v1 hash. Accepting a 32-byte one would publish a record
        // no consumer could read, and the failure would show up as a torrent that never resolves.
        Assert.Throws<ArgumentException>(() => Bep46Resolver.BuildRecord(InfoHash.CreateRandomV2()));
    }
}
