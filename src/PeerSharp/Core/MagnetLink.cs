using PeerSharp.Internals.Utilities;
using System.Web;

namespace PeerSharp.Core;

/// <summary>
/// Represents a parsed and validated magnet link.
/// Immutable value type that ensures the magnet link is valid upon construction.
/// </summary>
public sealed class MagnetLink : IEquatable<MagnetLink>
{
    private MagnetLink(
            InfoHash infoHash,
            InfoHash infoHashV2,
            string? displayName,
            IReadOnlyList<string> trackers,
            IReadOnlyList<string> exactSources,
            IReadOnlyList<System.Net.IPEndPoint> peers,
            IReadOnlyList<int> selectOnlyFileIndices,
            string originalString)
    {
        InfoHash = infoHash;
        InfoHashV2 = infoHashV2;
        DisplayName = displayName;
        Trackers = trackers;
        ExactSources = exactSources;
        Peers = peers;
        SelectOnlyFileIndices = selectOnlyFileIndices;
        OriginalString = originalString;
    }

    /// <summary>
    /// Gets the display name from the magnet link, if present.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the V1 info hash (SHA-1, 20 bytes). May be empty for V2-only magnets.
    /// </summary>
    public InfoHash InfoHash { get; }

    /// <summary>
    /// Gets the V2 info hash (SHA-256, 32 bytes). Empty for V1-only magnets.
    /// </summary>
    public InfoHash InfoHashV2 { get; }

    /// <summary>
    /// Gets whether this is a hybrid magnet (has both V1 and V2 hashes).
    /// </summary>
    public bool IsHybrid => IsV1 && IsV2;

    /// <summary>
    /// Gets whether this is a V1 magnet (has btih hash).
    /// </summary>
    public bool IsV1 => !InfoHash.IsEmpty;

    /// <summary>
    /// Gets whether this is a V2 magnet (has btmh hash).
    /// </summary>
    public bool IsV2 => !InfoHashV2.IsEmpty;

    /// <summary>
    /// Gets the original magnet link string.
    /// </summary>
    public string OriginalString { get; }

    /// <summary>
    /// Gets the list of tracker URLs from the magnet link.
    /// </summary>
    public IReadOnlyList<string> Trackers { get; }

    /// <summary>
    /// Gets the list of exact source URLs (xs=).
    /// </summary>
    public IReadOnlyList<string> ExactSources { get; }

    /// <summary>
    /// Gets the list of initial peers (x.pe) from the magnet link.
    /// </summary>
    public IReadOnlyList<System.Net.IPEndPoint> Peers { get; }

    /// <summary>
    /// BEP 53: Gets the file indices selected for download (so=), sorted ascending.
    /// Empty when the magnet link does not restrict the file selection.
    /// Indices refer to the torrent's user-visible file list once metadata is available.
    /// </summary>
    public IReadOnlyList<int> SelectOnlyFileIndices { get; }

    /// <summary>
    /// Implicitly converts a string to a MagnetLink by parsing it.
    /// </summary>
    /// <param name="magnetUri">The magnet link string.</param>
    public static implicit operator MagnetLink(string magnetUri) => Parse(magnetUri);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(MagnetLink? left, MagnetLink? right) => !(left == right);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(MagnetLink? left, MagnetLink? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Parses a magnet link string into a MagnetLink instance.
    /// </summary>
    /// <param name="magnetUri">The magnet link URI string.</param>
    /// <returns>A parsed MagnetLink instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when magnetUri is null.</exception>
    /// <exception cref="FormatException">Thrown when the magnet link is invalid.</exception>
    public static MagnetLink Parse(string magnetUri)
    {
        ArgumentNullException.ThrowIfNull(magnetUri);

        if (!TryParse(magnetUri, out var result, out var error))
        {
            throw new FormatException(error);
        }

        return result!;
    }

    /// <summary>
    /// Attempts to parse a magnet link string.
    /// </summary>
    /// <param name="magnetUri">The magnet link URI string.</param>
    /// <param name="result">The parsed MagnetLink if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(string? magnetUri, out MagnetLink? result)
    {
        return TryParse(magnetUri, out result, out _);
    }

    /// <summary>
    /// Attempts to parse a magnet link string with error details.
    /// </summary>
    /// <param name="magnetUri">The magnet link URI string.</param>
    /// <param name="result">The parsed MagnetLink if successful.</param>
    /// <param name="error">Error message if parsing failed.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(string? magnetUri, out MagnetLink? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(magnetUri))
        {
            error = "Magnet link cannot be null or empty.";
            return false;
        }

        if (!magnetUri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            error = "Magnet link must start with 'magnet:?'.";
            return false;
        }

        try
        {
            var uri = new Uri(magnetUri);
            var query = HttpUtility.ParseQueryString(uri.Query);

            var infoHash = InfoHash.Empty;
            var infoHashV2 = InfoHash.EmptyV2;
            string? displayName = null;
            var trackers = new List<string>();
            var exactSources = new List<string>();
            var peers = new List<System.Net.IPEndPoint>();

            // Parse all xt parameters
            var xtValues = query.GetValues("xt");
            if (xtValues != null)
            {
                foreach (var xt in xtValues)
                {
                    if (xt.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                    {
                        // V1 info hash (SHA-1, 20 bytes)
                        var hashStr = xt[9..];
                        if (hashStr.Length == 40) // Hex
                        {
                            if (InfoHash.TryFromHex(hashStr, out var hash))
                            {
                                infoHash = hash;
                            }
                        }
                        else if (hashStr.Length == 32) // Base32
                        {
                            try
                            {
                                infoHash = new InfoHash(Base32.Decode(hashStr));
                            }
                            catch
                            {
                                // Invalid base32 format - continue parsing
                            }
                        }
                    }
                    else if (xt.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
                    {
                        // BEP 52: V2 info hash (multihash format)
                        var multihash = xt[9..];
                        if (multihash.StartsWith("1220", StringComparison.OrdinalIgnoreCase) && multihash.Length == 68 && InfoHash.TryFromHex(multihash[4..], out var hashV2))
                        {
                            infoHashV2 = hashV2;
                        }
                    }
                }
            }

            if (infoHash.IsEmpty && infoHashV2.IsEmpty)
            {
                error = "Magnet link must contain a valid info hash (xt=urn:btih: or xt=urn:btmh:).";
                return false;
            }

            // Parse display name
            displayName = query["dn"];

            // Parse trackers
            var tr = query.GetValues("tr");
            if (tr != null)
            {
                foreach (var url in tr)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        trackers.Add(url);
                    }
                }
            }

            var xs = query.GetValues("xs");
            if (xs != null)
            {
                foreach (var url in xs)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        exactSources.Add(url);
                    }
                }
            }

            // Parse peers (BEP 9: x.pe=host:port)
            var xpe = query.GetValues("x.pe");
            if (xpe != null)
            {
                foreach (var entry in xpe)
                {
                    if (TryParseEndpoint(entry, out var ep))
                    {
                        peers.Add(ep);
                    }
                }
            }

            // Parse select-only file indices (BEP 53: so=0,2,4-7)
            var selectOnly = ParseSelectOnlyIndices(query.GetValues("so"));

            // Deduplicate trackers and peers
            var distinctTrackers = trackers.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
            var distinctSources = exactSources.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
            var distinctPeers = peers.Distinct().ToList().AsReadOnly();

            result = new MagnetLink(infoHash, infoHashV2, displayName, distinctTrackers, distinctSources, distinctPeers, selectOnly, magnetUri);
            return true;
        }
        catch (UriFormatException)
        {
            error = "Invalid magnet link URI format.";
            return false;
        }
    }

    /// <summary>
    /// BEP 53: Parses "so=" values of the form "0,2,4-7" into a sorted, deduplicated index list.
    /// Invalid tokens are ignored; the total number of indices is capped to guard against
    /// maliciously large ranges in untrusted magnet links.
    /// </summary>
    private static IReadOnlyList<int> ParseSelectOnlyIndices(string?[]? values)
    {
        const int MaxIndices = 10_000;

        if (values == null || values.Length == 0)
        {
            return Array.Empty<int>();
        }

        var indices = new SortedSet<int>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in value.Split(','))
            {
                var span = token.AsSpan().Trim();
                if (span.IsEmpty)
                {
                    continue;
                }

                int dash = span.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(span, out int index) && index >= 0)
                    {
                        indices.Add(index);
                    }
                }
                else if (int.TryParse(span[..dash], out int start) &&
                         int.TryParse(span[(dash + 1)..], out int end) &&
                         start >= 0 && end >= start)
                {
                    for (int i = start; i <= end; i++)
                    {
                        indices.Add(i);
                        if (indices.Count >= MaxIndices)
                        {
                            break;
                        }
                    }
                }

                if (indices.Count >= MaxIndices)
                {
                    return indices.ToList().AsReadOnly();
                }
            }
        }

        return indices.Count == 0 ? Array.Empty<int>() : indices.ToList().AsReadOnly();
    }

    private static bool TryParseEndpoint(string? value, out System.Net.IPEndPoint endpoint)
    {
        endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.None, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string host;
        string portStr;

        if (value[0] == '[')
        {
            int end = value.IndexOf(']');
            if (end <= 0 || end + 2 > value.Length || value[end + 1] != ':')
            {
                return false;
            }

            host = value[1..end];
            portStr = value[(end + 2)..];
        }
        else
        {
            int lastColon = value.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == value.Length - 1)
            {
                return false;
            }

            host = value[..lastColon];
            portStr = value[(lastColon + 1)..];
        }

        if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
        {
            return false;
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        endpoint = new System.Net.IPEndPoint(ip, port);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(MagnetLink? other)
    {
        return other is not null &&
        InfoHash.Equals(other.InfoHash) &&
        InfoHashV2.Equals(other.InfoHashV2);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as MagnetLink);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(InfoHash, InfoHashV2);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return OriginalString;
    }
}

