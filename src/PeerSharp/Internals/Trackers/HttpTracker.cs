using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Network;
using System.Buffers;
using System.Net;

namespace PeerSharp.Internals.Trackers;

internal class HttpTracker : TrackerBase, IDisposable
{
    private const int MaxTrackerResponseBytes = 1024 * 1024;
    private readonly ILogger<HttpTracker> _logger;
    private readonly IHttpClientFactory _httpClientFactory = new HttpClientFactory();
    private AtomicDisposal _disposal = new();
    private IHttpClient? _testClient;

    public HttpTracker()
        : this(NullLoggerFactory.Instance)
    {
    }

    public HttpTracker(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<HttpTracker>();
    }

    public override async Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
    {
        try
        {
            string url = BuildUrl(evt);
            _logger.LogDebug("Announcing to {Url}", url);

            var responseBytes = await GetResponseBytesAsync(url, ct).ConfigureAwait(false);
            var response = ParseResponse(responseBytes);

            // BEP 3: hold on to the session token for the next announce to this tracker.
            if (!string.IsNullOrEmpty(response.TrackerId))
            {
                _trackerId = response.TrackerId;
            }

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

            // BEP 31: a failure response can tell us when to come back. Only the tracker's own
            // failure carries that - a timeout or a transport error leaves the hint unset, and the
            // manager falls back to its exponential backoff.
            var failure = new AnnounceResponse
            {
                RetryHint = (ex as TrackerFailureException)?.RetryHint
            };
            RaiseAnnounceResult(false, failure, ex.Message);
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

            var responseBytes = await GetResponseBytesAsync(url, ct).ConfigureAwait(false);
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

            var responseBytes = await GetResponseBytesAsync(url, ct).ConfigureAwait(false);
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

    private async Task<byte[]> GetResponseBytesAsync(string url, CancellationToken ct)
    {
        var client = GetClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadContentWithLimitAsync(response.Content, MaxTrackerResponseBytes, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadContentWithLimitAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException($"Tracker response exceeds maximum size ({maxBytes} bytes).");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        int total = 0;
        try
        {
            while (true)
            {
                int remaining = maxBytes - total;
                int requestBytes = remaining <= 0 ? 1 : Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, requestBytes), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return ms.ToArray();
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidDataException($"Tracker response exceeds maximum size ({maxBytes} bytes).");
                }

                await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }



    /// <summary>
    /// BEP 3: a tracker may return {'failure reason': '...'} with no other keys. Surface that string
    /// rather than silently treating the empty response as success, and carry the BEP 31
    /// <c>retry in</c> hint that may accompany it.
    /// </summary>
    private static void ThrowIfFailure(BDict dict)
    {
        var failureReason = dict.GetString("failure reason");
        if (!string.IsNullOrEmpty(failureReason))
        {
            throw new TrackerFailureException(failureReason, TrackerRetryHint.TryParse(dict));
        }
    }

    private static AnnounceResponse ParseResponse(byte[] data)
    {
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            ThrowIfFailure(dict);

            var resp = new AnnounceResponse
            {
                Interval = (uint)(dict.GetLong("interval") ?? 600),
                LeechCount = (uint)(dict.GetLong("incomplete") ?? 0),
                SeedCount = (uint)(dict.GetLong("complete") ?? 0)
            };

            // Optional min interval (BEP 3/7 variants)
            var minInterval = dict.GetLong("min interval") ?? dict.GetLong("min_interval") ?? dict.GetLong("min_request_interval");
            if (minInterval > 0)
            {
                resp.MinInterval = (uint)minInterval.Value;
            }

            // BEP 3: an opaque session token. A tracker that issues one expects to see it again, and
            // one that never does may treat every announce as a fresh session.
            var trackerId = dict.GetString("tracker id");
            if (!string.IsNullOrEmpty(trackerId))
            {
                resp.TrackerId = trackerId;
            }

            // BEP 3: a tracker can complain and still answer. Kept apart from failure reason, because
            // the peers in this response are perfectly good.
            resp.WarningMessage = dict.GetString("warning message");

            // BEP 24: the tracker may report the address it saw us announce from, as a bare 4 byte
            // (IPv4) or 16 byte (IPv6) binary address with no port.
            var externalIp = dict.GetBytes("external ip");
            if (externalIp != null && externalIp.Value.Length is 4 or 16)
            {
                resp.ExternalIp = new IPAddress(externalIp.Value.Span);
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

    private ScrapeResponse ParseScrapeResponse(byte[] data)
    {
        // Response: d5:filesd20:...d8:completei5e...eee
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            ThrowIfFailure(dict);
        }
        if (node is BDict dict2 && dict2.Get("files") is BDict files)
        {
            // The files dict is keyed by the binary info hash. Like libtorrent, only
            // accept the entry for the hash we actually scraped - a response about a
            // different torrent must not be mistaken for ours.
            string expectedKey = System.Text.Encoding.Latin1.GetString(Torrent.InfoFile.Info.GetTrackerInfoHash().Span);
            if (files.Get(expectedKey) is BDict info)
            {
                return new ScrapeResponse
                {
                    SeedCount = (uint)(info.GetLong("complete") ?? 0),
                    LeechCount = (uint)(info.GetLong("incomplete") ?? 0),
                    Downloaded = (uint)(info.GetLong("downloaded") ?? 0)
                };
            }

            throw new InvalidDataException("Scrape response does not contain stats for the requested info hash");
        }
        throw new InvalidDataException("Invalid scrape response");
    }

    private static MultiScrapeResponse ParseMultiScrapeResponse(byte[] data)
    {
        var result = new MultiScrapeResponse();
        var node = BencodeParser.Parse(data);
        if (node is BDict dict)
        {
            ThrowIfFailure(dict);
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

        return baseUrl + "info_hash=" + UrlEncoding.Encode(Torrent.InfoFile.Info.GetTrackerInfoHash().Span);
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
            string lastPart = baseUrl[(index + 1)..];
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

    /// <summary>
    /// BEP 21: whether we are a partial seed - "a peer that is incomplete without downloading anything
    /// more", which happens when only some files of a multi-file torrent were selected. A fully complete
    /// torrent is a plain seed and announces normally.
    /// </summary>
    private bool IsPartialSeed()
    {
        // HasMetadata first: without metadata DataLeft reports 1 as a stand-in for "unknown", so a
        // magnet link still fetching its metadata would otherwise look like a partial seed and announce
        // paused before it had downloaded anything at all.
        return Torrent.HasMetadata && Torrent.SelectionFinished && Torrent.DataLeft > 0;
    }

    /// <summary>
    /// BEP 3 <c>tracker id</c>, remembered across announces to this tracker. Never cleared once set:
    /// the spec has trackers issue it to be quoted back, and a response that omits it is not a
    /// withdrawal.
    /// </summary>
    private string? _trackerId;

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

        AppendParam("info_hash", UrlEncoding.Encode(Torrent.InfoFile.Info.GetTrackerInfoHash().Span));
        AppendParam("peer_id", UrlEncoding.Encode(Torrent.Settings.PeerId));
        int listenPort = Torrent.PortListener?.Port ?? Torrent.Settings.Connection.TcpPort;
        AppendParam("port", listenPort.ToString());
        AppendParam("uploaded", Torrent.FileTransfer.Uploaded.ToString());
        AppendParam("downloaded", Torrent.FileTransfer.Downloaded.ToString());
        AppendParam("left", Torrent.DataLeft.ToString());
        AppendParam("compact", "1");
        // BEP 7: this parameter carries the client's own IPv6 address, so a tracker can hand it out to
        // IPv6-capable peers. It is not a flag, and "1" is not an address - a tracker that reads it
        // strictly gets nonsense and one that reads it loosely gets nothing, so it never did what the
        // old comment here claimed. Omitted entirely when this machine has no IPv6, which is the
        // honest answer and what every other client sends in that case.
        if (NetworkUtils.GetGlobalIPv6Address() is { } ownIPv6)
        {
            AppendParam("ipv6", ownIPv6.ToString());
        }
        AppendParam("numwant", Torrent.Settings.MaxPeersPerTrackerRequest.ToString());

        // BEP 3: quote back whatever this tracker last gave us, so it can tie our announces together.
        // Percent-encoded because the value is opaque and nothing stops it containing reserved
        // characters.
        if (!string.IsNullOrEmpty(_trackerId))
        {
            AppendParam("trackerid", Uri.EscapeDataString(_trackerId));
        }

        // BEP 21: a partial seed "MUST send an event=paused parameter in every announce while it is a
        // partial seed". Applied only where we would otherwise send no event - started, completed and
        // stopped are transitions the tracker needs, and replacing one of those with paused would lose
        // information rather than add it.
        var effectiveEvent = evt;
        if (evt == TrackerEvent.None && IsPartialSeed())
        {
            effectiveEvent = TrackerEvent.Paused;
        }

        if (effectiveEvent != TrackerEvent.None)
        {
            AppendParam("event", effectiveEvent.ToString().ToLower());
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
