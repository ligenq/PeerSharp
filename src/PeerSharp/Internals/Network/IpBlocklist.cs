using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Network;

/// <summary>
/// IP blocklist for filtering peer connections.
/// Loads P2P lists (Description:StartIP-EndIP), eMule DAT lists (StartIP - EndIP , level , Description),
/// CIDR notation and single addresses. Uses a sorted list of IP ranges for efficient O(log n) lookup.
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
    public void AddCidr(string cidr)
    {
        if (BlocklistParser.TryParseCidr(cidr, out var range))
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
    public void AddRange(IPAddress start, IPAddress end)
    {
        start = Normalize(start);
        end = Normalize(end);
        if (start.AddressFamily != end.AddressFamily || NetworkUtils.IpToUInt128(start) > NetworkUtils.IpToUInt128(end))
        {
            return;
        }

        lock (_lock)
        {
            _ranges.Add(new IpRange(start.AddressFamily, NetworkUtils.IpToUInt128(start), NetworkUtils.IpToUInt128(end)));
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

    /// <summary>
    /// Parses blocklist text a line at a time, staging ranges so a caller can commit a whole file at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lines are parsed as spans, without a string per line. The version this replaced built each line
    /// in a <c>StringBuilder</c> a character at a time, then trimmed, sliced and parsed it through
    /// <see cref="IPAddress"/>: 168 MB allocated and 115 ms for a 250,000-range list, which an
    /// application pays at startup.
    /// </para>
    /// <para>
    /// IPv4 octets are read as decimal even with leading zeros, as blocklist publishers and other
    /// clients mean them. <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> reads a leading zero
    /// as octal, so <c>010.000.000.001</c> became 8.0.0.1, <c>192.168.001.010</c> became 192.168.1.8,
    /// and an octet such as <c>008</c> failed the whole line. eMule DAT lists pad every octet that way.
    /// </para>
    /// </remarks>
    internal sealed class BlocklistParser
    {
        private const int MaxLineLength = 4096;

        // eMule's convention, which qBittorrent follows: an entry whose access level is above this
        // is allowed rather than blocked.
        private const int MaxBlockingAccessLevel = 127;

        private char[] _carry = new char[256];
        private int _carryLength;
        private int _characters;
        public List<IpRange> Ranges { get; } = [];

        public void Append(ReadOnlySpan<char> text)
        {
            if (text.Length > 64 * 1024 * 1024 - _characters) throw new InvalidDataException("Blocklist exceeds 64 MiB of text.");
            _characters += text.Length;
            while (!text.IsEmpty)
            {
                int newline = text.IndexOf('\n');
                if (newline < 0)
                {
                    Carry(text);
                    return;
                }

                if (_carryLength > 0)
                {
                    Carry(text[..newline]);
                    Add(_carry.AsSpan(0, _carryLength));
                    _carryLength = 0;
                }
                else
                {
                    if (newline > MaxLineLength) throw new InvalidDataException("Blocklist line is too long.");
                    Add(text[..newline]);
                }

                text = text[(newline + 1)..];
            }
        }

        public void Complete()
        {
            Add(_carry.AsSpan(0, _carryLength));
            _carryLength = 0;
        }

        private void Carry(ReadOnlySpan<char> text)
        {
            if (_carryLength + text.Length > MaxLineLength) throw new InvalidDataException("Blocklist line is too long.");
            if (_carryLength + text.Length > _carry.Length) Array.Resize(ref _carry, MaxLineLength);
            text.CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += text.Length;
        }

        private void Add(ReadOnlySpan<char> line)
        {
            if (TryParseLine(line, out var range))
            {
                if (Ranges.Count >= MaxRanges) throw new InvalidDataException("Blocklist has too many ranges.");
                Ranges.Add(range);
            }
        }

        internal static bool TryParseLine(ReadOnlySpan<char> line, out IpRange range)
        {
            range = default;
            line = line.Trim();
            if (line.IsEmpty || line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            // eMule DAT: "start - end , level , description". Only when what precedes the first comma
            // is a range: a P2P description can hold commas too ("Example, Inc:1.2.3.4-1.2.3.5").
            int comma = line.IndexOf(',');
            if (comma > 0)
            {
                var addresses = line[..comma];
                int dash = addresses.IndexOf('-');
                if (dash > 0 && TryParseRange(addresses[..dash].Trim(), addresses[(dash + 1)..].Trim(), out range))
                {
                    var rest = line[(comma + 1)..];
                    int nextComma = rest.IndexOf(',');
                    var level = (nextComma < 0 ? rest : rest[..nextComma]).Trim();
                    bool allowed = int.TryParse(level, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int access)
                        && access > MaxBlockingAccessLevel;
                    if (allowed) range = default;
                    return !allowed;
                }
            }

            // P2P: "description:start-end". IPv6 colons belong to the address, so the range is found
            // from its end, and the start is whatever after a colon parses as an address.
            int lastDash = line.LastIndexOf('-');
            if (lastDash > 0)
            {
                var end = line[(lastDash + 1)..].Trim();
                var left = line[..lastDash].Trim();
                if (TryParseRange(left, end, out range))
                {
                    return true;
                }

                for (int colon = left.IndexOf(':'); colon >= 0; colon = NextColon(left, colon))
                {
                    if (TryParseRange(left[(colon + 1)..].Trim(), end, out range))
                    {
                        return true;
                    }
                }
            }

            if (line.Contains('/'))
            {
                return TryParseCidr(line, out range);
            }

            if (TryParseAddress(line, out var family, out var single))
            {
                range = new IpRange(family, single, single);
                return true;
            }

            return false;
        }

        private static int NextColon(ReadOnlySpan<char> text, int colon)
        {
            int next = text[(colon + 1)..].IndexOf(':');
            return next < 0 ? -1 : colon + 1 + next;
        }

        private static bool TryParseRange(ReadOnlySpan<char> startText, ReadOnlySpan<char> endText, out IpRange range)
        {
            range = default;
            if (!TryParseAddress(startText, out var startFamily, out var start)
                || !TryParseAddress(endText, out var endFamily, out var end)
                || startFamily != endFamily || start > end)
            {
                return false;
            }

            range = new IpRange(startFamily, start, end);
            return true;
        }

        internal static bool TryParseCidr(ReadOnlySpan<char> text, out IpRange range)
        {
            range = default;
            int slash = text.IndexOf('/');
            if (slash <= 0 || !int.TryParse(text[(slash + 1)..].Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int prefix))
            {
                return false;
            }

            var addressText = text[..slash].Trim();
            AddressFamily family;
            UInt128 address;
            if (TryParseIPv4(addressText, out uint ipv4))
            {
                family = AddressFamily.InterNetwork;
                address = ipv4;
            }
            else if (addressText.Contains(':') && IPAddress.TryParse(addressText, out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // An IPv4-mapped prefix that covers only mapped space is the IPv4 range it maps; a
                // shorter one is an ordinary IPv6 range.
                if (ipv6.IsIPv4MappedToIPv6 && prefix is >= 96 and <= 128)
                {
                    family = AddressFamily.InterNetwork;
                    address = NetworkUtils.IpToUInt128(ipv6.MapToIPv4());
                    prefix -= 96;
                }
                else
                {
                    family = AddressFamily.InterNetworkV6;
                    address = NetworkUtils.IpToUInt128(ipv6);
                }
            }
            else
            {
                return false;
            }

            int bits = family == AddressFamily.InterNetwork ? 32 : 128;
            if (prefix > bits)
            {
                return false;
            }

            int hostBits = bits - prefix;
            UInt128 mask = hostBits == 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - 1;
            range = new IpRange(family, address & ~mask, address | mask);
            return true;
        }

        /// <summary>Parses an address, reading an IPv4-mapped IPv6 address as the IPv4 address it maps.</summary>
        private static bool TryParseAddress(ReadOnlySpan<char> text, out AddressFamily family, out UInt128 value)
        {
            if (TryParseIPv4(text, out uint ipv4))
            {
                family = AddressFamily.InterNetwork;
                value = ipv4;
                return true;
            }

            // Only IPv6 goes to IPAddress, which would otherwise also accept IPv4 in octal, in hex or
            // with octets missing.
            if (text.Contains(':') && IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address = Normalize(address);
                family = address.AddressFamily;
                value = NetworkUtils.IpToUInt128(address);
                return true;
            }

            family = default;
            value = default;
            return false;
        }

        /// <summary>Four decimal octets of up to three digits, leading zeros allowed.</summary>
        internal static bool TryParseIPv4(ReadOnlySpan<char> text, out uint value)
        {
            value = 0;
            int octets = 0;
            int i = 0;
            while (true)
            {
                int digits = 0;
                uint octet = 0;
                while (i < text.Length && (uint)(text[i] - '0') <= 9)
                {
                    if (++digits > 3) return false;
                    octet = (octet * 10) + (uint)(text[i] - '0');
                    i++;
                }

                if (digits == 0 || octet > 255) return false;
                value = (value << 8) | octet;
                if (++octets == 4) return i == text.Length;
                if (i >= text.Length || text[i] != '.') return false;
                i++;
            }
        }
    }

    internal readonly record struct IpRange(AddressFamily Family, UInt128 Start, UInt128 End);
}
