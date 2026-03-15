namespace PeerSharp.Tests;

public class TrackerStatusTests
{
    [Fact]
    public void Defaults_AreUnknownAndZeroed()
    {
        var status = new TrackerStatus("http://tracker");

        Assert.Equal("http://tracker", status.Url);
        Assert.Equal(TrackerStatusType.Unknown, status.Status);
        Assert.Equal(default, status.LastAnnounce);
        Assert.Equal(default, status.NextAnnounce);
        Assert.Equal(0, status.Interval);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastError);
        Assert.Equal(0u, status.SeedCount);
        Assert.Equal(0u, status.LeechCount);
    }

    [Fact]
    public void CanSetAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var next = now.AddMinutes(5);

        var status = new TrackerStatus(
            "udp://tracker",
            TrackerStatusType.Working,
            now,
            next,
            120,
            2,
            "ok",
            10,
            5);

        Assert.Equal(TrackerStatusType.Working, status.Status);
        Assert.Equal(now, status.LastAnnounce);
        Assert.Equal(next, status.NextAnnounce);
        Assert.Equal(120, status.Interval);
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.Equal("ok", status.LastError);
        Assert.Equal(10u, status.SeedCount);
        Assert.Equal(5u, status.LeechCount);
    }
}




