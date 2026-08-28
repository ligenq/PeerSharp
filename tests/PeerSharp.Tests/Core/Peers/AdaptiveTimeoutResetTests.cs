using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// The parts of the adaptive timeout that coverage showed nothing ever ran: the defaults it is
/// constructed with, resetting it, and the summary it reports.
/// </summary>
/// <remarks>
/// This is RFC 6298's round-trip estimator, and what it produces decides how long the engine waits
/// before giving up on a peer. Too short and healthy peers are dropped mid-transfer; too long and a
/// dead one holds a connection slot for half a minute.
/// </remarks>
public class AdaptiveTimeoutResetTests
{
    [Fact]
    public void TheDefaultConstructorUsesTheDocumentedBounds()
    {
        // The single-argument constructor is what the engine actually uses, and it had no test at
        // all, so nothing pinned the numbers it hands to every peer.
        var timeout = new AdaptiveTimeout(new FakeTimeProvider());

        Assert.Equal(10000, timeout.CurrentTimeoutMs);
        Assert.Equal(10000, timeout.SmoothedRttMs);
        Assert.Equal(0, timeout.SampleCount);

        // A single implausibly long sample must still be clamped to the 30 second ceiling.
        for (int i = 0; i < 10; i++)
        {
            timeout.RecordSuccess(120_000);
        }

        Assert.InRange(timeout.CurrentTimeoutMs, 1000, 30000);
    }

    [Fact]
    public void ResetReturnsItToAFreshEstimate()
    {
        var timeout = new AdaptiveTimeout(minTimeoutMs: 500, maxTimeoutMs: 9000, initialTimeoutMs: 4000, new FakeTimeProvider());
        var endpoint = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 6881);

        for (int i = 0; i < 5; i++)
        {
            timeout.RecordSuccess(40, endpoint);
        }

        Assert.True(timeout.HasHistory(endpoint));
        Assert.Equal(5, timeout.SampleCount);
        Assert.NotEqual(4000, timeout.SmoothedRttMs);

        timeout.Reset();

        // Everything the estimator learned is gone, including the per-endpoint history - which is
        // the half that would otherwise keep answering for endpoints seen before the reset.
        Assert.Equal(0, timeout.SampleCount);
        Assert.Equal(4000, timeout.SmoothedRttMs);
        Assert.Equal(0, timeout.RttVarianceMs);
        Assert.Equal(4000, timeout.CurrentTimeoutMs);
        Assert.False(timeout.HasHistory(endpoint));
        Assert.Equal(4000, timeout.GetTimeoutForEndpoint(endpoint));
    }

    [Fact]
    public void ResetLeavesItReadyToLearnAgain()
    {
        // The first sample after a reset has to be treated as the first sample, not folded into a
        // stale average - RFC 6298 initialises from it rather than smoothing towards it.
        var time = new FakeTimeProvider();
        var fresh = new AdaptiveTimeout(500, 9000, 4000, time);
        var reused = new AdaptiveTimeout(500, 9000, 4000, time);

        for (int i = 0; i < 4; i++)
        {
            reused.RecordSuccess(2000);
        }

        reused.Reset();

        fresh.RecordSuccess(60);
        reused.RecordSuccess(60);

        Assert.Equal(fresh.SmoothedRttMs, reused.SmoothedRttMs);
        Assert.Equal(fresh.CurrentTimeoutMs, reused.CurrentTimeoutMs);
    }

    [Fact]
    public void TheSummaryReportsTheCurrentState()
    {
        // Diagnostics only, but it reads private state under the lock, so it is the kind of thing
        // that quietly stops compiling into anything meaningful after a refactor.
        var timeout = new AdaptiveTimeout(new FakeTimeProvider());
        timeout.RecordSuccess(50, new IPEndPoint(IPAddress.Parse("203.0.113.5"), 51413));

        string summary = timeout.GetStatsSummary();

        Assert.Contains("SRTT=", summary, StringComparison.Ordinal);
        Assert.Contains($"Timeout={timeout.CurrentTimeoutMs}ms", summary, StringComparison.Ordinal);
        Assert.Contains($"Samples={timeout.SampleCount}", summary, StringComparison.Ordinal);
        Assert.Contains("Endpoints=1", summary, StringComparison.Ordinal);

        timeout.Reset();

        Assert.Contains("Samples=0", timeout.GetStatsSummary(), StringComparison.Ordinal);
        Assert.Contains("Endpoints=0", timeout.GetStatsSummary(), StringComparison.Ordinal);
    }
}
