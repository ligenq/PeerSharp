using PeerSharp.Core;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Equality for BEP 46 self-updating magnet links.
///
/// <para>
/// A self-updating link is addressed by a public key and an optional salt rather than by an info hash,
/// and <c>magnet:?xs=urn:btpk:...</c> carries no <c>btih</c> at all. Equality that looks only at the
/// hashes therefore compares every such link equal to every other, whatever they point at - and since
/// the hash code is derived from the same two fields, they collide in any set or dictionary as well.
/// </para>
/// </summary>
public class MagnetLinkBep46EqualityTests
{
    private static MagnetLink SelfUpdating(byte keyByte, byte? saltByte = null)
    {
        var key = new byte[32];
        Array.Fill(key, keyByte);

        string uri = $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(key)}";
        if (saltByte is not null)
        {
            var salt = new byte[8];
            Array.Fill(salt, saltByte.Value);
            uri += $"&s={Convert.ToHexStringLower(salt)}";
        }

        Assert.True(MagnetLink.TryParse(uri, out var magnet), $"Failed to parse {uri}");
        return magnet!;
    }

    [Fact]
    public void DifferentPublicKeysAreNotEqual()
    {
        var first = SelfUpdating(0x01);
        var second = SelfUpdating(0x02);

        Assert.True(first.IsSelfUpdating && second.IsSelfUpdating);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DifferentSaltsUnderOneKeyAreNotEqual()
    {
        // BEP 46 exists precisely so one publisher key can address several records by salt.
        var first = SelfUpdating(0x01, saltByte: 0xAA);
        var second = SelfUpdating(0x01, saltByte: 0xBB);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ASaltedLinkIsNotEqualToTheUnsaltedOneWithTheSameKey()
    {
        Assert.NotEqual(SelfUpdating(0x01), SelfUpdating(0x01, saltByte: 0xAA));
    }

    [Fact]
    public void TheSameLinkParsedTwiceIsEqualAndHashesAlike()
    {
        var first = SelfUpdating(0x07, saltByte: 0x42);
        var second = SelfUpdating(0x07, saltByte: 0x42);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    /// <summary>
    /// The consequence that bites: a set of distinct publishers collapsing to one entry.
    /// </summary>
    [Fact]
    public void DistinctSelfUpdatingLinksSurviveInASet()
    {
        var links = new HashSet<MagnetLink>
        {
            SelfUpdating(0x01),
            SelfUpdating(0x02),
            SelfUpdating(0x01, saltByte: 0xAA),
            SelfUpdating(0x01, saltByte: 0xBB),
        };

        Assert.Equal(4, links.Count);
    }

    /// <summary>
    /// The ordinary case must keep working: links addressed by info hash are still compared by it.
    /// </summary>
    [Fact]
    public void InfoHashLinksAreStillComparedByHash()
    {
        var hash = InfoHash.CreateRandom();
        Assert.True(MagnetLink.TryParse($"magnet:?xt=urn:btih:{hash.ToHexString()}", out var first));
        Assert.True(MagnetLink.TryParse($"magnet:?xt=urn:btih:{hash.ToHexString()}&dn=other", out var second));
        Assert.True(MagnetLink.TryParse($"magnet:?xt=urn:btih:{InfoHash.CreateRandom().ToHexString()}", out var third));

        Assert.Equal(first, second);
        Assert.Equal(first!.GetHashCode(), second!.GetHashCode());
        Assert.NotEqual(first, third);
    }
}
