namespace PeerSharp.Internals.Trackers;

/// <summary>
/// BEP 48: Response for multi-hash scrape requests.
/// Maps info hash to scrape statistics.
/// </summary>
internal class MultiScrapeResponse
{
    public Dictionary<string, ScrapeResponse> Results { get; set; } = [];
}
