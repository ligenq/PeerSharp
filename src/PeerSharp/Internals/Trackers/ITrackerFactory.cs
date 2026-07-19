using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.Internals.Trackers;

internal interface ITrackerFactory
{
    ITracker? CreateTracker(string url, TimeProvider timeProvider);
}

internal class TrackerFactory : ITrackerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public TrackerFactory()
        : this(NullLoggerFactory.Instance)
    {
    }

    public TrackerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ITracker? CreateTracker(string url, TimeProvider timeProvider)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpTracker(_loggerFactory);
        }
        else if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            return new UdpTracker(timeProvider, _loggerFactory);
        }
        return null;
    }
}
