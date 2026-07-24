using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Internals.Network;

/// <summary>
/// IP blocklist for filtering peer connections.
/// Supports loading from P2P format files (Description:StartIP-EndIP) and CIDR notation.
/// Uses a sorted list of IP ranges for efficient O(log n) lookup.
/// </summary>
internal class IpBlocklist
{
    private readonly Lock _lock = new();
    private readonly ILogger<IpBlocklist> _logger;
    private readonly List<IpRange> _ranges = [];
    private bool _sorted;

    public IpBlocklist()
        : this(NullLoggerFactory.Instance)
    {
    }

    public IpBlocklist(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<IpBlocklist>();
    }

    /// <summary>
    /// Gets or sets whether the blocklist is enabled.
    /// Defaults to false until data is loaded via <see cref="LoadFromStream"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets the number of IP ranges in the blocklist.
    /// </summary>
    public int RangeCount
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Count;
            }
        }
    }

    /// <summary>
    /// Adds a CIDR range to the blocklist.
    /// </summary>
    public void AddCidr(string cidr, string? description = null)
    {
        if (NetworkUtils.TryParseCidr(cidr, out var start, out var end))
        {
            lock (_lock)
            {
                _ranges.Add(new IpRange(start, end, description));
                _sorted = false;
            }
        }
    }

    /// <summary>
    /// Adds a single IP range to the blocklist.
    /// </summary>
    public void AddRange(IPAddress start, IPAddress end, string? description = null)
    {
        if (start.AddressFamily != end.AddressFamily)
        {
            return;
        }

        lock (_lock)
        {
            _ranges.Add(new IpRange(NetworkUtils.IpToUInt128(start), NetworkUtils.IpToUInt128(end), description));
            _sorted = false;
        }
    }

    /// <summary>
    /// Clears all ranges from the blocklist and disables filtering.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _ranges.Clear();
            _sorted = true;
        }
        Enabled = false;
    }

    /// <summary>
    /// Checks if an IP address is blocked.
    /// </summary>
    /// <param name="address">The IP address to check.</param>
    /// <returns>True if the IP is blocked, false otherwise.</returns>
    public bool IsBlocked(IPAddress address)
    {
        if (!Enabled || address == null)
        {
            return false;
        }

        var ip = NetworkUtils.IpToUInt128(address);

        lock (_lock)
        {
            EnsureSorted();
            return BinarySearchContains(ip);
        }
    }

    /// <summary>
    /// Checks if an IP address is blocked.
    /// </summary>
    /// <param name="address">The IP address string to check.</param>
    /// <returns>True if the IP is blocked, false otherwise.</returns>
    public bool IsBlocked(string address)
    {
        if (!Enabled || string.IsNullOrEmpty(address))
        {
            return false;
        }

        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        return IsBlocked(ip);
    }

    /// <summary>
    /// Checks if an endpoint is blocked.
    /// </summary>
    public bool IsBlocked(IPEndPoint? endpoint)
    {
        if (!Enabled || endpoint == null)
        {
            return false;
        }

        return IsBlocked(endpoint.Address);
    }

    /// <summary>
    /// Loads a blocklist from a stream and enables blocklist filtering.
    /// </summary>
    /// <param name="stream">Stream containing blocklist data.</param>
    /// <returns>Number of ranges loaded.</returns>
    public int LoadFromStream(Stream stream)
    {
        var parsed = new List<IpRange>();
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryParseLine(line, out var range))
                {
                    parsed.Add(range);
                }
            }

            Commit(parsed);
            _logger.LogInformation("Loaded {Count} IP ranges from stream", parsed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading blocklist from stream");
            return 0;
        }

        return parsed.Count;
    }

    /// <summary>
    /// Loads a blocklist from a stream asynchronously and enables blocklist filtering.
    /// </summary>
    /// <param name="stream">Stream containing blocklist data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of ranges loaded.</returns>
    public async Task<int> LoadFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Ranges are staged in a local list and only published once the whole stream has been
        // read. A cancelled or failed load therefore leaves the existing blocklist untouched,
        // instead of arming filtering with whatever half of the file happened to arrive.
        var parsed = new List<IpRange>();
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (TryParseLine(line, out var range))
                {
                    parsed.Add(range);
                }
            }

            Commit(parsed);
            _logger.LogInformation("Loaded {Count} IP ranges from stream", parsed.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading blocklist from stream");
            return 0;
        }

        return parsed.Count;
    }

    /// <summary>
    /// Publishes a fully parsed batch of ranges and enables filtering.
    /// </summary>
    private void Commit(List<IpRange> parsed)
    {
        lock (_lock)
        {
            _ranges.AddRange(parsed);
            _sorted = false;
        }

        Enabled = true;
    }

    private bool BinarySearchContains(UInt128 ip)
    {
        if (_ranges.Count == 0)
        {
            return false;
        }

        int left = 0;
        int right = _ranges.Count - 1;

        while (left <= right)
        {
            int mid = left + ((right - left) / 2);
            var range = _ranges[mid];

            if (ip < range.Start)
            {
                right = mid - 1;
            }
            else if (ip > range.End)
            {
                left = mid + 1;
            }
            else
            {
                // ip >= range.Start && ip <= range.End
                return true;
            }
        }

        return false;
    }

    private void EnsureSorted()
    {
        if (_sorted)
        {
            return;
        }

        _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Coalesce overlapping and adjacent ranges. Binary search over ranges sorted
        // by Start only returns correct results when the ranges are disjoint: with
        // overlapping/nested ranges (common in real P2P blocklists) a containing range
        // with an earlier Start could be skipped, producing false negatives.
        int write = 0;
        for (int read = 1; read < _ranges.Count; read++)
        {
            var current = _ranges[write];
            var next = _ranges[read];

            // Merge if next starts within, or immediately after, the current range.
            // The `End + 1` adjacency check is guarded against UInt128 overflow.
            bool adjacent = current.End != UInt128.MaxValue && next.Start <= current.End + 1;
            if (next.Start <= current.End || adjacent)
            {
                var mergedEnd = next.End > current.End ? next.End : current.End;
                _ranges[write] = current with { End = mergedEnd };
            }
            else
            {
                _ranges[++write] = next;
            }
        }

        if (_ranges.Count > 0)
        {
            _ranges.RemoveRange(write + 1, _ranges.Count - write - 1);
        }

        _sorted = true;
    }

    /// <summary>
    /// Parses one blocklist line into a range without publishing it, so callers can stage a
    /// whole file and commit it atomically.
    /// </summary>
    private static bool TryParseLine(string line, out IpRange range)
    {
        range = default;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        line = line.Trim();

        // Skip comments
        if (line.StartsWith('#') || line.StartsWith("//"))
        {
            return false;
        }

        // Try P2P format: Description:StartIP-EndIP
        int colonIndex = line.LastIndexOf(':');
        if (colonIndex > 0)
        {
            string ipPart = line[(colonIndex + 1)..];
            string description = line[..colonIndex];

            int dashIndex = ipPart.IndexOf('-');
            if (dashIndex > 0)
            {
                string startStr = ipPart[..dashIndex].Trim();
                string endStr = ipPart[(dashIndex + 1)..].Trim();

                if (IPAddress.TryParse(startStr, out var startIp) &&
                    IPAddress.TryParse(endStr, out var endIp) &&
                    startIp.AddressFamily == endIp.AddressFamily)
                {
                    range = new IpRange(NetworkUtils.IpToUInt128(startIp), NetworkUtils.IpToUInt128(endIp), description);
                    return true;
                }
            }
        }

        // Try CIDR format: 192.168.1.0/24
        if (line.Contains('/') && NetworkUtils.TryParseCidr(line, out var start, out var end))
        {
            range = new IpRange(start, end, null);
            return true;
        }

        // Try single IP
        if (IPAddress.TryParse(line, out var singleIp))
        {
            var ipValue = NetworkUtils.IpToUInt128(singleIp);
            range = new IpRange(ipValue, ipValue, null);
            return true;
        }

        return false;
    }

    private readonly record struct IpRange(UInt128 Start, UInt128 End, string? Description);
}
