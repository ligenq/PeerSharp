using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Network;

namespace PeerSharp.Internals.Trackers;

internal interface ITrackerFactory
{
    ITracker? CreateTracker(string url, TimeProvider timeProvider);
}

internal class TrackerFactory : ITrackerFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostAddressCache? _hostAddresses;
    private readonly ILoggerFactory _loggerFactory;

    public TrackerFactory(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, HostAddressCache? hostAddresses = null)
    {
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _hostAddresses = hostAddresses;
    }

    public ITracker? CreateTracker(string url, TimeProvider timeProvider)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpTracker(_loggerFactory, _httpClientFactory, _hostAddresses ?? new HostAddressCache(timeProvider));
        }
        else if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            return new UdpTracker(timeProvider, _loggerFactory, _hostAddresses ?? new HostAddressCache(timeProvider));
        }
        return null;
    }
}
