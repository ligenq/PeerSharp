namespace PeerSharp.Internals.Trackers;

internal interface ITrackerFactory
{
    ITracker? CreateTracker(string url, TimeProvider timeProvider);
}

internal class TrackerFactory : ITrackerFactory
{
    public ITracker? CreateTracker(string url, TimeProvider timeProvider)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpTracker();
        }
        else if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            return new UdpTracker(timeProvider);
        }
        return null;
    }
}
