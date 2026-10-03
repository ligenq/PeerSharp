using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
    private bool _enabled;
    private long _clearGeneration;
    private const int MaxRanges = 1_000_000;

    /// <summary>
    /// Immutable published view of <see cref="_ranges"/>, sorted and coalesced. Readers take it
    /// with a single volatile read and never touch the lock: <see cref="IsBlocked(IPAddress)"/>
    /// runs once per inbound connection and once per discovered peer, and those arrive in
    /// parallel, so a shared lock on the read path removed all concurrency rather than protecting
    /// anything. Mutations rebuild and republish under <see cref="_lock"/>; null means stale.
    /// </summary>
    private IpRange[]? _snapshot;

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
    public bool Enabled { get => Volatile.Read(ref _enabled); set => Volatile.Write(ref _enabled, value); }

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
        if (TryParseCidrRange(cidr, description, out var range))
        {
            lock (_lock)
            {
                _ranges.Add(range);
                _sorted = false;
                Volatile.Write(ref _snapshot, null);
            }
        }
    }

    /// <summary>
    /// Adds a single IP range to the blocklist.
    /// </summary>
    public void AddRange(IPAddress start, IPAddress end, string? description = null)
    {
        start = Normalize(start);
        end = Normalize(end);
        if (start.AddressFamily != end.AddressFamily || NetworkUtils.IpToUInt128(start) > NetworkUtils.IpToUInt128(end))
        {
            return;
        }

        lock (_lock)
        {
            _ranges.Add(new IpRange(start.AddressFamily, NetworkUtils.IpToUInt128(start), NetworkUtils.IpToUInt128(end), description));
            _sorted = false;
            Volatile.Write(ref _snapshot, null);
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
            _clearGeneration++;
            _sorted = true;
            Volatile.Write(ref _snapshot, []);
            Enabled = false;
        }
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

        address = Normalize(address);
        var ip = NetworkUtils.IpToUInt128(address);
        var snapshot = Volatile.Read(ref _snapshot) ?? BuildSnapshot();
        return BinarySearchContains(snapshot, address.AddressFamily, ip);
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
        long generation;
        lock (_lock) generation = _clearGeneration;
        var parser = new BlocklistParser();
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            char[] buffer = new char[4096];
            int count;
            while ((count = reader.Read(buffer)) > 0) parser.Append(buffer.AsSpan(0, count));
            parser.Complete();
            if (!Commit(parser.Ranges, generation)) return 0;
            _logger.LogInformation("Loaded {Count} IP ranges from stream", parser.Ranges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading blocklist from stream");
            return 0;
        }

        return parser.Ranges.Count;
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
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (_lock) generation = _clearGeneration;
        var parser = new BlocklistParser();
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                parser.Append(buffer.AsSpan(0, count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            parser.Complete();
            cancellationToken.ThrowIfCancellationRequested();
            if (!Commit(parser.Ranges, generation)) return 0;
            _logger.LogInformation("Loaded {Count} IP ranges from stream", parser.Ranges.Count);
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

        return parser.Ranges.Count;
    }

    /// <summary>
    /// Publishes a fully parsed batch of ranges and enables filtering.
    /// </summary>
    private bool Commit(List<IpRange> parsed, long generation)
    {
        lock (_lock)
        {
            if (generation != _clearGeneration) return false;
            if (parsed.Count > MaxRanges - _ranges.Count) throw new InvalidDataException("Blocklist has too many ranges.");
            _ranges.AddRange(parsed);
            _sorted = false;
            Volatile.Write(ref _snapshot, null);
            Enabled = true;
        }
        return true;
    }

    /// <summary>
    /// Rebuilds the published snapshot. Only reached on the first lookup after a mutation; the
    /// second check under the lock keeps concurrent readers from each building their own.
    /// </summary>
    private IpRange[] BuildSnapshot()
    {
        lock (_lock)
        {
            var existing = _snapshot;
            if (existing != null)
            {
                return existing;
            }

            EnsureSorted();
            var built = _ranges.ToArray();
            Volatile.Write(ref _snapshot, built);
            return built;
        }
    }

    private static bool BinarySearchContains(IpRange[] ranges, AddressFamily family, UInt128 ip)
    {
        if (ranges.Length == 0)
        {
            return false;
        }

        int left = 0;
        int right = ranges.Length - 1;

        while (left <= right)
        {
            int mid = left + ((right - left) / 2);
            var range = ranges[mid];

            if ((int)family < (int)range.Family || (family == range.Family && ip < range.Start))
            {
                right = mid - 1;
            }
            else if ((int)family > (int)range.Family || (family == range.Family && ip > range.End))
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

        _ranges.Sort((a, b) => a.Family != b.Family ? a.Family.CompareTo(b.Family) : a.Start.CompareTo(b.Start));

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
            if (next.Family == current.Family && (next.Start <= current.End || adjacent))
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

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseCidrRange(string cidr, string? description, out IpRange range)
    {
        range = default;
        int slash = cidr.IndexOf('/');
        if (slash <= 0 || !IPAddress.TryParse(cidr[..slash].Trim(), out var address)) return false;
        if (address.IsIPv4MappedToIPv6 && int.TryParse(cidr[(slash + 1)..], out int prefix) && prefix is >= 96 and <= 128)
        {
            address = address.MapToIPv4();
            cidr = $"{address}/{prefix - 96}";
        }
        if (!NetworkUtils.TryParseCidr(cidr, out var start, out var end)) return false;
        range = new IpRange(address.AddressFamily, start, end, description);
        return true;
    }

    private sealed class BlocklistParser
    {
        private readonly StringBuilder _line = new();
        private int _characters;
        public List<IpRange> Ranges { get; } = [];

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
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            // IPv6 colons belong to the address, so find the range before its optional description.
            int dashIndex = line.LastIndexOf('-');
            if (dashIndex > 0 && IPAddress.TryParse(line[(dashIndex + 1)..].Trim(), out var endIp))
            {
                string left = line[..dashIndex].Trim();
                string? description = null;
                if (!IPAddress.TryParse(left, out var startIp))
                {
                    for (int colon = left.IndexOf(':'); colon >= 0; colon = left.IndexOf(':', colon + 1))
                    {
                        if (!IPAddress.TryParse(left[(colon + 1)..].Trim(), out startIp)) continue;
                        description = left[..colon];
                        break;
                    }
                }
                if (startIp != null)
                {
                    startIp = Normalize(startIp);
                    endIp = Normalize(endIp);
                    UInt128 start = NetworkUtils.IpToUInt128(startIp), end = NetworkUtils.IpToUInt128(endIp);
                    if (startIp.AddressFamily == endIp.AddressFamily && start <= end)
                    {
                        range = new IpRange(startIp.AddressFamily, start, end, description);
                        return true;
                    }
                }
            }

            // Try CIDR format: 192.168.1.0/24
            if (TryParseCidrRange(line, null, out range)) return true;

            // Try single IP
            if (IPAddress.TryParse(line, out var singleIp))
            {
                singleIp = Normalize(singleIp);
                var ipValue = NetworkUtils.IpToUInt128(singleIp);
                range = new IpRange(singleIp.AddressFamily, ipValue, ipValue, null);
                return true;
            }

            return false;
        }

        public void Append(ReadOnlySpan<char> text)
        {
            if (text.Length > 64 * 1024 * 1024 - _characters) throw new InvalidDataException("Blocklist exceeds 64 MiB of text.");
            _characters += text.Length;
            foreach (char value in text)
            {
                if (value == '\n') Complete();
                else
                {
                    if (_line.Length >= 4096) throw new InvalidDataException("Blocklist line is too long.");
                    _line.Append(value);
                }
            }
        }

        public void Complete()
        {
            if (TryParseLine(_line.ToString(), out var range))
            {
                if (Ranges.Count >= MaxRanges) throw new InvalidDataException("Blocklist has too many ranges.");
                Ranges.Add(range);
            }
            _line.Clear();
        }
    }

    private readonly record struct IpRange(AddressFamily Family, UInt128 Start, UInt128 End, string? Description);
}
