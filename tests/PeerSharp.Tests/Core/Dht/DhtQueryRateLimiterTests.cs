using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// The per-source budget on inbound DHT queries.
///
/// <para>
/// A DHT node answers strangers by design. Without a budget that is two problems at once: anyone can
/// spend our CPU and sockets from outside, and the node works as a reflector, because a
/// <c>get_peers</c> reply is much larger than the query and the source address of a UDP datagram is
/// whatever the sender wrote in it.
/// </para>
/// </summary>
public class DhtQueryRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static DhtQueryRateLimiter Create(FakeTimeProvider time, int perAddress = 3, int maxAddresses = 100, int fallback = 600)
    {
        return new DhtQueryRateLimiter(
            time,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            perAddress,
            Window,
            maxAddresses,
            fallback);
    }

    [Fact]
    public void QueriesWithinTheBudget_AreAllowed()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        var source = IPAddress.Parse("198.51.100.1");

        Assert.True(limiter.IsQueryAllowed(source));
        Assert.True(limiter.IsQueryAllowed(source));
        Assert.True(limiter.IsQueryAllowed(source));
    }

    [Fact]
    public void QueriesBeyondTheBudget_AreDropped()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        var source = IPAddress.Parse("198.51.100.1");

        for (int i = 0; i < 3; i++)
        {
            Assert.True(limiter.IsQueryAllowed(source));
        }

        Assert.False(limiter.IsQueryAllowed(source));
        Assert.False(limiter.IsQueryAllowed(source));
        Assert.Equal(2, limiter.DroppedQueries);
    }

    [Fact]
    public void TheBudgetIsPerAddress()
    {
        // One noisy source must not cost everyone else their answers.
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        var noisy = IPAddress.Parse("198.51.100.1");
        var quiet = IPAddress.Parse("198.51.100.2");

        for (int i = 0; i < 4; i++)
        {
            limiter.IsQueryAllowed(noisy);
        }

        Assert.False(limiter.IsQueryAllowed(noisy));
        Assert.True(limiter.IsQueryAllowed(quiet));
    }

    [Fact]
    public void TheBudgetRefillsWhenTheWindowCloses()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        var source = IPAddress.Parse("198.51.100.1");

        for (int i = 0; i < 3; i++)
        {
            limiter.IsQueryAllowed(source);
        }

        Assert.False(limiter.IsQueryAllowed(source));

        time.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.True(limiter.IsQueryAllowed(source));
    }

    [Fact]
    public void IPv6SourcesAreBudgetedSeparately()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time);
        var v4 = IPAddress.Parse("198.51.100.1");
        var v6 = IPAddress.Parse("2001:db8::1");

        for (int i = 0; i < 4; i++)
        {
            limiter.IsQueryAllowed(v4);
        }

        Assert.False(limiter.IsQueryAllowed(v4));
        Assert.True(limiter.IsQueryAllowed(v6));
    }

    [Fact]
    public void ClosedWindowsAreDroppedFromTheTable()
    {
        // The limiter must not become the exhaustion vector it exists to close: one entry per source
        // address, kept forever, is a slow leak driven from outside.
        var time = new FakeTimeProvider();
        var limiter = Create(time);

        for (int i = 1; i <= 50; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        Assert.Equal(50, limiter.TrackedAddresses);

        time.Advance(Window + TimeSpan.FromSeconds(1));
        limiter.Prune();

        Assert.Equal(0, limiter.TrackedAddresses);
    }

    [Fact]
    public void TheTrackingTableIsBounded()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 3, maxAddresses: 10);

        for (int i = 1; i <= 40; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        Assert.True(limiter.TrackedAddresses <= 10, $"tracked {limiter.TrackedAddresses} addresses, expected at most 10");
    }

    [Fact]
    public void AFullTableStillAnswersUntrackedSources()
    {
        // Source addresses on UDP are forgeable. If a full table simply meant "refuse", anyone able
        // to forge addresses could deny service to the addresses they forged, so an untracked source
        // is still answered - out of a shared allowance rather than unconditionally.
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 1, maxAddresses: 5, fallback: 10);

        for (int i = 1; i <= 5; i++)
        {
            Assert.True(limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}")));
        }

        Assert.True(limiter.IsQueryAllowed(IPAddress.Parse("203.0.113.7")));
        Assert.Equal(0, limiter.DroppedQueries);
    }

    [Fact]
    public void AFullTableDoesNotBecomeABypass()
    {
        // The attack this closes: fill the table with invented addresses, then flood spoofed as one
        // chosen victim. Every one of those queries is untracked, so without a shared allowance they
        // would all be answered and the limit would exist in name only.
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 1, maxAddresses: 5, fallback: 10);

        for (int i = 1; i <= 5; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        var victim = IPAddress.Parse("203.0.113.7");
        int answered = 0;
        for (int i = 0; i < 50; i++)
        {
            if (limiter.IsQueryAllowed(victim))
            {
                answered++;
            }
        }

        Assert.Equal(10, answered);
        Assert.Equal(40, limiter.DroppedQueries);
    }

    [Fact]
    public void TheSharedAllowanceIsPerWindow()
    {
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 1, maxAddresses: 5, fallback: 2);

        for (int i = 1; i <= 5; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        var untracked = IPAddress.Parse("203.0.113.7");
        Assert.True(limiter.IsQueryAllowed(untracked));
        Assert.True(limiter.IsQueryAllowed(untracked));
        Assert.False(limiter.IsQueryAllowed(untracked));

        // The tracked windows close together with the shared one, so the next query is tracked
        // normally again rather than drawing on the allowance.
        time.Advance(Window + TimeSpan.FromSeconds(1));
        Assert.True(limiter.IsQueryAllowed(untracked));
    }

    [Fact]
    public void TheSharedAllowanceRefillsWhileTheTableStaysFull()
    {
        // Sustained pressure, not a single burst. If the shared allowance never reset, a node that
        // stayed busy across a window boundary would answer no untracked source again, ever - the
        // limiter would have turned into a permanent outage rather than a rate limit.
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 1, maxAddresses: 5, fallback: 2);

        for (int i = 1; i <= 5; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        var untracked = IPAddress.Parse("203.0.113.7");
        Assert.True(limiter.IsQueryAllowed(untracked));
        Assert.True(limiter.IsQueryAllowed(untracked));
        Assert.False(limiter.IsQueryAllowed(untracked));

        // The window closes, and a fresh wave of sources fills the table straight back up - so the
        // next untracked query still has no slot and must come out of the allowance.
        time.Advance(Window + TimeSpan.FromSeconds(1));
        for (int i = 1; i <= 5; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"192.0.2.{i}"));
        }

        Assert.Equal(5, limiter.TrackedAddresses);
        Assert.True(limiter.IsQueryAllowed(untracked), "the shared allowance should have refilled with the new window");
        Assert.True(limiter.IsQueryAllowed(untracked));
        Assert.False(limiter.IsQueryAllowed(untracked));
    }

    [Fact]
    public void AFullTableIsStillBoundedAtItsCapacity()
    {
        // Drawing on the shared allowance must not quietly add entries: that would let the table grow
        // past the bound it exists to enforce.
        var time = new FakeTimeProvider();
        var limiter = Create(time, perAddress: 1, maxAddresses: 5, fallback: 100);

        for (int i = 1; i <= 5; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"198.51.100.{i}"));
        }

        for (int i = 1; i <= 30; i++)
        {
            limiter.IsQueryAllowed(IPAddress.Parse($"203.0.113.{i}"));
        }

        Assert.Equal(5, limiter.TrackedAddresses);
    }

    [Fact]
    public void ARejectedConstructionArgumentIsCaughtAtTheConstructor()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, perAddress: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, maxAddresses: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, fallback: 0));
    }
}
