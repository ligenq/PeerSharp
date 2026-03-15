using PeerSharp.Internals.Peers;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

public class AdaptiveTimeoutTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void CurrentTimeoutMs_InitialState_ReturnsInitialValue()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);

        // Act & Assert
        Assert.Equal(10000, adaptive.CurrentTimeoutMs);
        Assert.Equal(0, adaptive.SampleCount);
    }

    [Fact]
    public void RecordSuccess_SingleSample_UpdatesStats()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);

        // Act
        adaptive.RecordSuccess(200); // 200ms

        // Assert
        Assert.Equal(1, adaptive.SampleCount);
        // On first sample: SRTT = sample, VAR = sample / 2
        // Sample is 200, but minTimeout is 1000, so SRTT is clamped to 500.
        Assert.Equal(500.0, adaptive.SmoothedRttMs);
        Assert.Equal(100.0, adaptive.RttVarianceMs);
        // Sample count is 1 < 3, so should still return initial timeout
        Assert.Equal(10000, adaptive.CurrentTimeoutMs);
    }

    [Fact]
    public void CurrentTimeoutMs_ThreeSamples_CalculatesTimeout()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);

        // Act
        adaptive.RecordSuccess(500); // SRTT=500, VAR=250
        adaptive.RecordSuccess(500); // Diff=0. VAR = 0.75*250 + 0.25*0 = 187.5. SRTT = 0.875*500 + 0.125*500 = 500.
        adaptive.RecordSuccess(500); // Diff=0. VAR = 0.75*187.5 + 0.25*0 = 140.625. SRTT = 500.

        // Assert
        Assert.Equal(3, adaptive.SampleCount);
        // Timeout = SRTT + 4 * VAR = 500 + 4 * 140.625 = 500 + 562.5 = 1062.5 -> 1062
        Assert.Equal(1062, adaptive.CurrentTimeoutMs);
    }

    [Fact]
    public void CurrentTimeoutMs_HighLatencySamples_CalculatesHigherTimeout()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);

        // Act
        adaptive.RecordSuccess(2000); // SRTT=2000, VAR=1000
        adaptive.RecordSuccess(2000); // SRTT=2000, VAR=750
        adaptive.RecordSuccess(2000); // SRTT=2000, VAR=562.5

        // Assert
        // Timeout = 2000 + 4 * 562.5 = 2000 + 2250 = 4250.
        Assert.Equal(4250, adaptive.CurrentTimeoutMs);
    }

    [Fact]
    public void RecordTimeout_IncreasesVariance()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);
        adaptive.RecordSuccess(2000);
        adaptive.RecordSuccess(2000);
        adaptive.RecordSuccess(2000);
        double initialVar = adaptive.RttVarianceMs;

        // Act
        adaptive.RecordTimeout();

        // Assert
        Assert.True(adaptive.RttVarianceMs > initialVar);
        Assert.Equal(initialVar * 1.1, adaptive.RttVarianceMs);
    }

    [Fact]
    public void GetTimeoutForEndpoint_WithHistory_ReturnsEndpointSpecificTimeout()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);
        var ep = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 80);

        // Act
        adaptive.RecordSuccess(5000, ep);
        adaptive.RecordSuccess(5000, ep);
        adaptive.RecordSuccess(5000, ep);

        // Assert
        Assert.True(adaptive.HasHistory(ep));
        // Endpoint Timeout = 5000 + 4 * (5000/2 * 0.75 * 0.75) = 5000 + 4 * 1406.25 = 5000 + 5625 = 10625.
        Assert.Equal(10625, adaptive.GetTimeoutForEndpoint(ep));
    }

    [Fact]
    public void GetConservativeTimeoutMs_TenSamples_Returns95thPercentile()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);
        for (int i = 1; i <= 10; i++)
        {
            adaptive.RecordSuccess(i * 100); // 100, 200, ..., 1000
        }

        // Act
        int conservative = adaptive.GetConservativeTimeoutMs();

        // Assert
        // 95th percentile of 10 samples is index 9 (the 10th sample) = 1000ms.
        // Conservative = 1000 * 1.5 = 1500ms.
        Assert.Equal(1500, conservative);
    }

    [Fact]
    public void GetAggressiveTimeoutMs_FiveSamples_ReturnsMedianBased()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);
        adaptive.RecordSuccess(100);
        adaptive.RecordSuccess(200);
        adaptive.RecordSuccess(300);
        adaptive.RecordSuccess(400);
        adaptive.RecordSuccess(500);

        // Act
        int aggressive = adaptive.GetAggressiveTimeoutMs();

        // Assert
        // Median is 300. Aggressive = 300 * 2.0 = 600. 
        // Clamped to minTimeoutMs (1000).
        Assert.Equal(1000, aggressive);
    }

    [Fact]
    public void CleanupOldEndpointStats_EntriesExpired_RemovesThem()
    {
        // Arrange
        var adaptive = new AdaptiveTimeout(1000, 30000, 10000, _timeProvider);
        for (int i = 0; i < 1001; i++)
        {
            // Valid IP: 1.1.X.Y where i = X*256 + Y
            adaptive.RecordSuccess(200, new IPEndPoint(new IPAddress(new byte[] { 1, 1, (byte)(i >> 8), (byte)(i & 0xFF) }), 80));
        }

        // Advance time by 31 minutes
        _timeProvider.Advance(TimeSpan.FromMinutes(31));

        // Act
        // This should trigger cleanup during a new RecordSuccess
        adaptive.RecordSuccess(200, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 80));

        // Assert
        // We can't directly check the dictionary size, but we can check if old EP has history
        Assert.False(adaptive.HasHistory(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 80)));
    }
}





