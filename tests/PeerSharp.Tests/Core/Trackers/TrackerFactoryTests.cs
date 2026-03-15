using PeerSharp.Internals.Trackers;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core.Trackers;

public class TrackerFactoryTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Theory]
    [InlineData("http://tracker.com/announce", typeof(HttpTracker))]
    [InlineData("https://tracker.com/announce", typeof(HttpTracker))]
    [InlineData("udp://tracker.com:80/announce", typeof(UdpTracker))]
    public void CreateTracker_ValidUrl_ReturnsCorrectType(string url, Type expectedType)
    {
        var factory = new TrackerFactory();
        var tracker = factory.CreateTracker(url, _timeProvider);

        Assert.NotNull(tracker);
        Assert.IsType(expectedType, tracker);
    }

    [Fact]
    public void CreateTracker_InvalidUrl_ReturnsNull()
    {
        var factory = new TrackerFactory();
        var tracker = factory.CreateTracker("ftp://invalid.com", _timeProvider);

        Assert.Null(tracker);
    }
}





