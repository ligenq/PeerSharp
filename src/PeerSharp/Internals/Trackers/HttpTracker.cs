using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Internals.Trackers;

internal class HttpTracker : TrackerBase, IDisposable
{
    private readonly ILogger<HttpTracker> _logger = TorrentLoggerFactory.CreateLogger<HttpTracker>();
    private readonly IHttpClientFactory _httpClientFactory = new HttpClientFactory();
    private AtomicDisposal _disposal = new();
    private IHttpClient? _testClient;

    public HttpTracker()
    {
    }

    public override async Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
    {
        try
        {
            string url = BuildUrl(evt);
            _logger.LogDebug("Announcing to {Url}", url);

            var client = GetClient();
            var responseBytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            var response = ParseResponse(responseBytes);

            RaiseAnnounceResult(true, response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Task was cancelled by the manager, ignore
            throw;
        }
        catch (Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
            {
                _logger.LogInformation("Announce to {Url} failed: Timeout or Cancelled", Url);
            }
            else
            {
                _logger.LogWarning(ex, "Announce to {Url} failed", Url);
            }
            RaiseAnnounceResult(false, new AnnounceResponse(), ex.Message);
        }
    }

    public override void Deinit()
    {
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public override async Task ScrapeAsync(CancellationToken ct)
    {
        try
        {
            string url = BuildScrapeUrl();
            if (string.IsNullOrEmpty(url))
            {
                _logger.LogDebug("Scrape not supported (url format)");
                return;
            }

            _logger.LogDebug("Scraping {Url}", url);

            var client = GetClient();
            var responseBytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            var response = ParseScrapeResponse(responseBytes);

            RaiseScrapeResult(true, response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
            {
                _logger.LogInformation("Scrape for {Url} failed: Timeout or Cancelled", Url);
            }
            else
            {
                _logger.LogWarning(ex, "Scrape failed");
            }
            RaiseScrapeResult(false, new ScrapeResponse());
        }
    }

    public override async Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct)
    {
        if (infoHashes == null || infoHashes.Count == 0)
        {
            return;
        }

        try
        {
            string url = BuildScrapeUrl(infoHashes);
            if (string.IsNullOrEmpty(url))
            {
                _logger.LogDebug("Multi-scrape not supported (url format)");
                return;
            }

            _logger.LogDebug("Multi-scraping {Url}", url);

            var client = GetClient();
            var responseBytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            var response = ParseMultiScrapeResponse(responseBytes);

            RaiseMultiScrapeResult(true, response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
            {
                _logger.LogInformation("Multi-scrape for {Url} failed: Timeout or Cancelled", Url);
            }
            else
            {
                _logger.LogWarning(ex, "Multi-scrape failed");
            }
            RaiseMultiScrapeResult(false, new MultiScrapeResponse());
        }
    }

    internal void SetTestClient(IHttpClient client)
    {
        _testClient = client;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            // No resources to dispose - SharedClient is static and shouldn't be disposed
        }
    }



    private static AnnounceResponse ParseResponse(byte[] data)
    {
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            // BEP 3: a tracker may return {'failure reason': '...'} with no other keys.
            // Surface that string rather than silently treating the empty response as success.
            var failureReason = dict.GetString("failure reason");
            if (!string.IsNullOrEmpty(failureReason))
            {
                throw new InvalidDataException($"Tracker returned failure: {failureReason}");
            }

            var resp = new AnnounceResponse
            {
                Interval = (uint)(dict.GetLong("interval") ?? 600),
                LeechCount = (uint)(dict.GetLong("incomplete") ?? 0),
                SeedCount = (uint)(dict.GetLong("complete") ?? 0)
            };

            // Optional min interval (BEP 3/7 variants)
            var minInterval = dict.GetLong("min interval") ?? dict.GetLong("min_interval") ?? dict.GetLong("min_request_interval");
            if (minInterval.HasValue && minInterval.Value > 0)
            {
                resp.MinInterval = (uint)minInterval.Value;
            }

            // BEP 23: Compact IPv4 peers (6 bytes each: 4 IP + 2 port)
            var peers = dict.GetBytes("peers");
            if (peers != null)
            {
                var span = peers.Value.Span;
                for (int i = 0; i <= span.Length - 6; i += 6)
                {
                    var ip = new IPAddress(span.Slice(i, 4));
                    int port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span.Slice(i + 4, 2));
                    resp.Peers.Add(new IPEndPoint(ip, port));
                }
            }

            // BEP 7: Compact IPv6 peers (18 bytes each: 16 IP + 2 port)
            var peers6 = dict.GetBytes("peers6");
            if (peers6 != null)
            {
                var span = peers6.Value.Span;
                for (int i = 0; i <= span.Length - 18; i += 18)
                {
                    var ip = new IPAddress(span.Slice(i, 16));
                    int port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span.Slice(i + 16, 2));
                    resp.Peers.Add(new IPEndPoint(ip, port));
                }
            }

            // Non-compact peers (dictionary list) - rare but possible, ignored for now
            return resp;
        }
        throw new InvalidDataException("Invalid response");
    }

    private static ScrapeResponse ParseScrapeResponse(byte[] data)
    {
        // Response: d5:filesd20:...d8:completei5e...eee
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            var failureReason = dict.GetString("failure reason");
            if (!string.IsNullOrEmpty(failureReason))
            {
                throw new InvalidDataException($"Tracker returned failure: {failureReason}");
            }
        }
        if (node is BDict dict2 && dict2.Get("files") is BDict files)
        {
            // We only care about our infohash
            foreach (var key in files.Dict.Keys)
            {
                // Check if key corresponds to our hash?
                // For now, if we requested one hash, we take the first result.
                if (files.Get(key) is BDict info)
                {
                    var resp = new ScrapeResponse
                    {
                        SeedCount = (uint)(info.GetLong("complete") ?? 0),
                        LeechCount = (uint)(info.GetLong("incomplete") ?? 0),
                        Downloaded = (uint)(info.GetLong("downloaded") ?? 0)
                    };
                    return resp;
                }
            }
        }
        throw new InvalidDataException("Invalid scrape response");
    }

    private static MultiScrapeResponse ParseMultiScrapeResponse(byte[] data)
    {
        var result = new MultiScrapeResponse();
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            var failureReason = dict.GetString("failure reason");
            if (!string.IsNullOrEmpty(failureReason))
            {
                throw new InvalidDataException($"Tracker returned failure: {failureReason}");
            }
        }
        if (node is BDict dict2 && dict2.Get("files") is BDict files)
        {
            foreach (var kvp in files.Dict)
            {
                if (kvp.Value is not BDict info)
                {
                    continue;
                }

                var hashBytes = System.Text.Encoding.Latin1.GetBytes(kvp.Key);
                var resp = new ScrapeResponse
                {
                    SeedCount = (uint)(info.GetLong("complete") ?? 0),
                    LeechCount = (uint)(info.GetLong("incomplete") ?? 0),
                    Downloaded = (uint)(info.GetLong("downloaded") ?? 0)
                };

                result.Results[Convert.ToHexString(hashBytes)] = resp;
            }
        }

        return result;
    }

    private string BuildScrapeUrl()
    {
        var baseUrl = BuildScrapeBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
        {
            return string.Empty;
        }

        return baseUrl + "info_hash=" + UrlEncoding.Encode(Torrent.InfoFile.Info.Hash.Span);
    }

    private string BuildScrapeUrl(IReadOnlyList<InfoHash> infoHashes)
    {
        var baseUrl = BuildScrapeBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(baseUrl);
        bool first = true;
        foreach (var hash in infoHashes)
        {
            if (hash.Length != InfoHash.V1Length)
            {
                continue;
            }

            if (!first)
            {
                sb.Append('&');
            }
            first = false;

            sb.Append("info_hash=");
            sb.Append(UrlEncoding.Encode(hash.Span));
        }

        return first ? string.Empty : sb.ToString();
    }

    private string BuildScrapeBaseUrl()
    {
        string baseUrl = Url;
        int index = baseUrl.LastIndexOf('/');
        if (index >= 0)
        {
            string lastPart = baseUrl.Substring(index + 1);
            if (lastPart.StartsWith("announce"))
            {
                string scrapeBase = string.Concat(baseUrl.AsSpan(0, index + 1), "scrape", lastPart.AsSpan(8));
                if (!scrapeBase.Contains('?'))
                {
                    scrapeBase += "?";
                }
                else if (!scrapeBase.EndsWith('?') && !scrapeBase.EndsWith('&'))
                {
                    scrapeBase += "&";
                }

                return scrapeBase;
            }
        }
        return string.Empty;
    }

    private string BuildUrl(TrackerEvent evt)
    {
        // Build query manually to avoid double-encoding of percent-encoded info_hash/peer_id
        var sb = new System.Text.StringBuilder();

        void AppendParam(string key, string value)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
        }

        AppendParam("info_hash", UrlEncoding.Encode(Torrent.InfoFile.Info.Hash.Span));
        AppendParam("peer_id", UrlEncoding.Encode(Torrent.Settings.PeerId));
        AppendParam("port", Torrent.Settings.Connection.TcpPort.ToString());
        AppendParam("uploaded", Torrent.FileTransfer.Uploaded.ToString());
        AppendParam("downloaded", Torrent.FileTransfer.Downloaded.ToString());
        AppendParam("left", Torrent.DataLeft.ToString());
        AppendParam("compact", "1");
        AppendParam("ipv6", "1"); // BEP 7: Request IPv6 peers
        AppendParam("numwant", Torrent.Settings.MaxPeersPerTrackerRequest.ToString());

        if (evt != TrackerEvent.None)
        {
            AppendParam("event", evt.ToString().ToLower());
        }

        var baseUrl = Url;
        if (!baseUrl.Contains('?'))
        {
            baseUrl += "?";
        }
        else if (!baseUrl.EndsWith('?') && !baseUrl.EndsWith('&'))
        {
            baseUrl += "&";
        }

        return baseUrl + sb.ToString();
    }

    private IHttpClient GetClient()
    {
        if (_testClient != null)
        {
            return _testClient;
        }

        var settings = Torrent.Settings.Proxy;
        if (!settings.ProxyTrackers)
        {
            // Create a temporary settings object for "None" proxy to force direct connection
            // We can't modify the global settings object as it might be used elsewhere
            var directSettings = new ProxySettings { Type = ProxyType.None };
            return new DefaultHttpClient(_httpClientFactory.CreateClient(directSettings, true));
        }

        return new DefaultHttpClient(_httpClientFactory.CreateClient(settings, true));
    }
}
