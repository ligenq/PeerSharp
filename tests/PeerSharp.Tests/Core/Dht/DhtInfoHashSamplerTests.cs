using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Dht;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 51: the sampler's contract is that the subset it returns is stable for the interval it
/// advertises, and that the interval it advertises counts down to the rotation rather than restarting
/// on every query.
/// </summary>
public class DhtInfoHashSamplerTests
{
    private static string[] Keys(int count)
    {
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = InfoHash.CreateRandom().ToHexStringUpper();
        }

        return keys;
    }

    private static IReadOnlyList<string> Decode(byte[] samples)
    {
        var hashes = new List<string>();
        for (int i = 0; i < samples.Length; i += 20)
        {
            hashes.Add(Convert.ToHexString(samples.AsSpan(i, 20)));
        }

        return hashes;
    }

    [Fact]
    public void EmptyStore_ReturnsAnEmptySampleAndZeroNum()
    {
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider());

        var sample = sampler.Take([]);

        Assert.Empty(sample.Samples);
        Assert.Equal(0, sample.Num);
    }

    [Fact]
    public void SmallStore_ReturnsEveryHash()
    {
        var keys = Keys(5);
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider());

        var sample = sampler.Take(keys);

        Assert.Equal(5, sample.Num);
        Assert.Equal([.. keys.Order()], [.. Decode(sample.Samples).Order()]);
    }

    [Fact]
    public void LargeStore_IsCappedButStillReportsTheRealTotal()
    {
        // num is how an indexer learns there is more to collect than one datagram can carry.
        var keys = Keys(500);
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider());

        var sample = sampler.Take(keys);

        Assert.Equal(DhtInfoHashSampler.MaxSamplesPerResponse, Decode(sample.Samples).Count);
        Assert.Equal(500, sample.Num);
        Assert.All(Decode(sample.Samples), hash => Assert.Contains(hash, keys));
    }

    [Fact]
    public void Samples_AreDistinct()
    {
        var keys = Keys(500);
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider());

        var sampled = Decode(sampler.Take(keys).Samples);

        Assert.Equal(sampled.Count, sampled.Distinct().Count());
    }

    [Fact]
    public void RepeatedQueriesWithinTheInterval_ReturnTheSameSubset()
    {
        // The whole point of advertising an interval is that asking again inside it is pointless.
        var keys = Keys(500);
        var time = new FakeTimeProvider();
        var sampler = new DhtInfoHashSampler(time);

        var first = Decode(sampler.Take(keys).Samples);
        time.Advance(TimeSpan.FromHours(1));
        var second = Decode(sampler.Take(keys).Samples);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Interval_CountsDownTowardsTheRotation()
    {
        var keys = Keys(500);
        var time = new FakeTimeProvider();
        var sampler = new DhtInfoHashSampler(time, TimeSpan.FromHours(6));

        Assert.Equal(6 * 3600, sampler.Take(keys).IntervalSeconds);

        time.Advance(TimeSpan.FromHours(2));

        // An indexer arriving two hours in should be told four hours, not six.
        Assert.Equal(4 * 3600, sampler.Take(keys).IntervalSeconds);
    }

    [Fact]
    public void AfterTheInterval_TheSubsetRotates()
    {
        // 500 hashes and 20 per sample: drawing the same 20 twice would be astronomically unlikely,
        // so an unchanged sample here means rotation is broken rather than unlucky.
        var keys = Keys(500);
        var time = new FakeTimeProvider();
        var sampler = new DhtInfoHashSampler(time, TimeSpan.FromHours(6));

        var first = Decode(sampler.Take(keys).Samples);
        time.Advance(TimeSpan.FromHours(6));
        var second = sampler.Take(keys);

        Assert.NotEqual(first, Decode(second.Samples));
        Assert.Equal(6 * 3600, second.IntervalSeconds);
    }

    [Fact]
    public void AnEmptySampleIsNotCached()
    {
        // A node asked before it stored anything must not keep answering "nothing" for six hours.
        var time = new FakeTimeProvider();
        var sampler = new DhtInfoHashSampler(time, TimeSpan.FromHours(6));

        Assert.Empty(sampler.Take([]).Samples);

        var keys = Keys(3);
        var sample = sampler.Take(keys);

        Assert.Equal(3, Decode(sample.Samples).Count);
    }

    [Fact]
    public void IntervalAboveTheSpecCeiling_IsRejectedInFavourOfTheCeiling()
    {
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider(), TimeSpan.FromDays(7));

        Assert.Equal(
            (int)DhtInfoHashSampler.MaxRefreshInterval.TotalSeconds,
            sampler.Take(Keys(1)).IntervalSeconds);
    }

    [Fact]
    public void NonInfoHashKeys_AreSkipped()
    {
        // The peer cache is keyed by string; anything not decodable as a 20 byte hash cannot go into
        // a BEP 51 sample, which is defined as a run of fixed-width hashes.
        var valid = InfoHash.CreateRandom().ToHexStringUpper();
        var sampler = new DhtInfoHashSampler(new FakeTimeProvider());

        var sample = sampler.Take([valid, "not-hex", "AABB"]);

        Assert.Equal([valid], Decode(sample.Samples));
        Assert.Equal(3, sample.Num);
    }
}
