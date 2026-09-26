using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Resolves host names for trackers, remembering the answer - including "no such host" - for a while.
///
/// <para>
/// The reason is exceptions rather than latency. <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>
/// reports a name that does not resolve by throwing, and a torrent routinely lists trackers whose
/// names stopped resolving years ago. Every announce to one of those threw, once per address family,
/// and each throw travelled up through HttpClient's connect path and the tracker's own handlers - a
/// stream of first-chance exceptions for a condition that was known after the first one. Here the
/// failure is thrown once per host per <see cref="NotFoundLifetime"/> and callers get an empty list,
/// which lets them skip the announce, or one address family of it, without throwing anything.
/// </para>
///
/// <para>
/// It also folds together lookups that happen at once: an announce goes out over IPv4 and IPv6 in
/// parallel, and both want the same name.
/// </para>
/// </summary>
internal sealed class HostAddressCache
{
    /// <summary>How long a successful answer is reused. Short enough to follow a tracker that moves.</summary>
    internal static readonly TimeSpan FoundLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a failed lookup is reused. Kept short because a failure can be the network rather than
    /// the name - a laptop resolving before its connection is up - and a tracker should not stay
    /// unreachable long after the network returns. It is still longer than the first steps of the
    /// tracker backoff, which is what makes a dead name cost one exception rather than one per retry.
    /// </summary>
    internal static readonly TimeSpan NotFoundLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Entries held before expired ones are swept. Trackers number in the tens.</summary>
    internal const int SweepThreshold = 256;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookupAsync;
    private readonly TimeProvider _timeProvider;

    public HostAddressCache(TimeProvider timeProvider)
        : this(timeProvider, static (host, ct) => Dns.GetHostAddressesAsync(host, ct))
    {
    }

    internal HostAddressCache(TimeProvider timeProvider, Func<string, CancellationToken, Task<IPAddress[]>> lookupAsync)
    {
        _timeProvider = timeProvider;
        _lookupAsync = lookupAsync;
    }

    /// <summary>
    /// The addresses <paramref name="host"/> resolves to, or none when it does not resolve. Only the
    /// caller's own cancellation is thrown.
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var now = _timeProvider.GetUtcNow();
        if (!_entries.TryGetValue(host, out var entry) || entry.IsExpired(now))
        {
            if (_entries.Count >= SweepThreshold)
            {
                Sweep(now);
            }

            var fresh = new Entry(() => LookupAsync(host), now);
            entry = _entries.AddOrUpdate(host, fresh, (_, existing) => existing.IsExpired(now) ? fresh : existing);
        }

        // The lookup is shared, so one caller giving up must not cancel it for the others.
        return await entry.Addresses.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IPAddress[]> LookupAsync(string host)
    {
        try
        {
            return await _lookupAsync(host, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            // Not a well-formed host name at all; it will not become one later.
            return [];
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (host, entry) in _entries)
        {
            if (entry.IsExpired(now))
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(host, entry));
            }
        }
    }

    private sealed class Entry
    {
        private const int Pending = 0;
        private const int Found = 1;
        private const int NotFound = 2;

        private readonly DateTimeOffset _started;
        private readonly Lazy<Task<IPAddress[]>> _addresses;
        private int _outcome;

        public Entry(Func<Task<IPAddress[]>> lookupAsync, DateTimeOffset started)
        {
            _started = started;
            // Entries can lose the AddOrUpdate race. Only the selected entry starts a lookup,
            // and concurrent callers of that entry share the same task.
            _addresses = new Lazy<Task<IPAddress[]>>(() => RecordAsync(lookupAsync));
        }

        public Task<IPAddress[]> Addresses => _addresses.Value;

        public bool IsExpired(DateTimeOffset now)
        {
            var lifetime = Volatile.Read(ref _outcome) switch
            {
                // A lookup still in flight is shared, never repeated.
                Pending => Timeout.InfiniteTimeSpan,
                Found => FoundLifetime,
                _ => NotFoundLifetime,
            };
            return lifetime != Timeout.InfiniteTimeSpan && now - _started >= lifetime;
        }

        private async Task<IPAddress[]> RecordAsync(Func<Task<IPAddress[]>> lookupAsync)
        {
            var addresses = await lookupAsync().ConfigureAwait(false);
            Volatile.Write(ref _outcome, addresses.Length > 0 ? Found : NotFound);
            return addresses;
        }
    }
}
