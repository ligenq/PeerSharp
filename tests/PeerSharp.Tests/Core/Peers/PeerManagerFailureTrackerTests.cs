using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerFailureTrackerTests
{
    [Fact]
    public void Record_PrunesFailuresOutsideTheRollingWindow()
    {
        var tracker = new PeerManagerFailureTracker();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Record(start);
        var afterWindow = tracker.Record(start.AddMinutes(1).AddSeconds(1));
        var secondRecent = tracker.Record(start.AddMinutes(1).AddSeconds(2));
        var thirdRecent = tracker.Record(start.AddMinutes(1).AddSeconds(3));

        Assert.False(afterWindow.ShouldEscalate);
        Assert.Equal(1, afterWindow.RecentCount);
        Assert.False(secondRecent.ShouldEscalate);
        Assert.True(thirdRecent.ShouldEscalate);
        Assert.Equal(3, thirdRecent.RecentCount);
    }

    [Fact]
    public void Record_RateLimitsEscalationWithinTheWindow()
    {
        var tracker = new PeerManagerFailureTracker();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Record(start);
        tracker.Record(start.AddSeconds(1));
        var escalation = tracker.Record(start.AddSeconds(2));
        var suppressed = tracker.Record(start.AddSeconds(30));

        Assert.True(escalation.ShouldEscalate);
        Assert.False(suppressed.ShouldEscalate);
        Assert.Equal(4, tracker.TotalFailures);
    }
}
