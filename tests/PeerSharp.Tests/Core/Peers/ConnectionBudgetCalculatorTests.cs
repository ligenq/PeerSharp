using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Sharing one connection attempt's timeout budget across the transports it may try.
///
/// <para>
/// A dial has a single budget covering its whole plan. An unbounded first attempt spends the lot and
/// leaves the fallback the configured minimum - one second by default, which is not long enough to
/// reach most peers over the open internet, so the fallback fails for a reason unrelated to the peer.
/// Only uTP used to be capped, so Utp-&gt;Tcp split ten seconds into three and seven while Tcp-&gt;Utp
/// gave TCP all ten and uTP the floor. On a live swarm 168 connections took that second path and 98%
/// timed out, roughly half of every failed attempt in the run.
/// </para>
/// </summary>
public class ConnectionBudgetCalculatorTests
{
    private const int Total = 10000;
    private const int Cap = 3000;
    private const int Floor = 1000;

    /// <summary>
    /// The case that was broken. Both orders must divide the budget the same way, because which
    /// transport happens to be tried first says nothing about how long either one deserves.
    /// </summary>
    [Fact]
    public void BothTransportOrdersSplitTheBudgetIdentically()
    {
        // First attempt of a two-transport plan.
        int first = ConnectionBudgetCalculator.ForAttempt(Total, hasFallback: true, Cap);
        Assert.Equal(Cap, first);

        // What the fallback inherits.
        int remaining = ConnectionBudgetCalculator.Remaining(Total, first, Floor);
        int second = ConnectionBudgetCalculator.ForAttempt(remaining, hasFallback: false, Cap);

        Assert.Equal(Total - Cap, second);
        Assert.True(
            second > Floor,
            $"The fallback was left {second}ms, the configured minimum. An unbounded first attempt has " +
            "consumed the budget, so the fallback cannot reach any peer that is not already nearby.");
    }

    /// <summary>
    /// The single-transport case must be unaffected: with nothing behind it, an attempt may use
    /// everything it has.
    /// </summary>
    [Fact]
    public void APlanWithOneTransportUsesTheWholeBudget()
    {
        Assert.Equal(Total, ConnectionBudgetCalculator.ForAttempt(Total, hasFallback: false, Cap));
    }

    /// <summary>
    /// The floor is a floor, not a target: it applies only when the arithmetic would go below it.
    /// </summary>
    [Fact]
    public void RemainingNeverFallsBelowTheFloor()
    {
        Assert.Equal(Floor, ConnectionBudgetCalculator.Remaining(Total, Total, Floor));
        Assert.Equal(Floor, ConnectionBudgetCalculator.Remaining(500, 400, Floor));
        Assert.Equal(7000, ConnectionBudgetCalculator.Remaining(Total, Cap, Floor));
    }

    [Fact]
    public void TheCapNeverExceedsTheBudgetOrDropsBelowTheFloor()
    {
        // A cap larger than the whole budget would leave the fallback nothing.
        Assert.Equal(Total, ConnectionBudgetCalculator.FallbackCap(Total, configuredCapMs: 30000, Floor));

        // A cap below the floor would make the first attempt useless instead of the second.
        Assert.Equal(Floor, ConnectionBudgetCalculator.FallbackCap(Total, configuredCapMs: 10, Floor));

        Assert.Equal(Cap, ConnectionBudgetCalculator.FallbackCap(Total, Cap, Floor));
    }

    /// <summary>
    /// Walks a whole two-transport plan the way PeerManager does, which is the shape that actually
    /// regressed - the parts are individually reasonable and it was their composition that failed.
    /// </summary>
    [Fact]
    public void AFullTwoTransportPlanLeavesBothAttemptsUsable()
    {
        int cap = ConnectionBudgetCalculator.FallbackCap(Total, Cap, Floor);
        int remaining = Total;
        var granted = new List<int>();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool hasFallback = attempt < 1;
            int budget = ConnectionBudgetCalculator.ForAttempt(remaining, hasFallback, cap);
            granted.Add(budget);
            remaining = ConnectionBudgetCalculator.Remaining(remaining, budget, Floor);
        }

        Assert.Equal([3000, 7000], granted);
        Assert.All(granted, b => Assert.True(b > Floor, $"An attempt was granted only {b}ms."));
    }
}
