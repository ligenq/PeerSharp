using System.Net;
using System.Net.Sockets;
using PeerSharp.Core;

namespace PeerSharp.Internals.Network;

internal interface IHttpClientFactory
{
    /// <summary>
    /// Default HTTP connections held open per origin. Deliberately modest: a tracker needs one, and
    /// a caller that wants more parallelism against a single host says so explicitly.
    /// </summary>
    const int DefaultMaxConnectionsPerServer = 10;

    /// <summary>
    /// Returns a pooled client for this configuration. The client belongs to the factory and must not
    /// be disposed by the caller; it stays valid until the factory is disposed, or until this
    /// configuration is evicted (see <see cref="HttpClientFactory.MaxCachedClients"/>).
    /// </summary>
    HttpClient CreateClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress = null,
        AddressFamily? addressFamily = null,
        int maxConnectionsPerServer = DefaultMaxConnectionsPerServer);
}

/// <summary>
/// Pools <see cref="HttpClient"/> instances by configuration, so trackers and web seeds reuse
/// connections instead of paying a handshake per announce.
///
/// <para>
/// The pool belongs to whoever constructs the factory - in practice the engine - rather than to the
/// process. A static cache outlived every engine that used it, kept each engine's proxy credentials
/// reachable for as long as the process ran, and silently shared one set of connection pools and one
/// per-server limit between engines that had each configured their own. Disposing the factory
/// disposes every client it made.
/// </para>
/// </summary>
internal sealed class HttpClientFactory : IHttpClientFactory, IDisposable
{
    /// <summary>
    /// How many distinct client configurations are held at once. Ordinary use needs a handful: a
    /// proxy choice, tracker or not, an optional bind address and address family. The cap is for the
    /// case that is not ordinary - settings rewritten repeatedly while the engine runs - so a long
    /// session cannot accumulate clients, and with them connection pools, without limit.
    /// </summary>
    internal const int MaxCachedClients = 16;

    /// <summary>How many resolved addresses one connect may try before giving up.</summary>
    private const int MaxConnectAttempts = 3;

    private readonly Dictionary<HttpClientKey, PooledClient> _clients = [];
    private readonly Lock _lock = new();
    private AtomicDisposal _disposal = new();
    private long _useCounter;

    public HttpClient CreateClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress = null,
        AddressFamily? addressFamily = null,
        int maxConnectionsPerServer = IHttpClientFactory.DefaultMaxConnectionsPerServer)
    {
        if (bindAddress != null && addressFamily != null && bindAddress.AddressFamily != addressFamily)
        {
            throw new ArgumentException("The requested address family must match the bind address.", nameof(addressFamily));
        }

        int perServer = Math.Clamp(maxConnectionsPerServer, 1, 256);

        // Structured, not interpolated. The old string key joined the fields with a separator the
        // fields themselves could contain, so a username of "a|b" with password "c" keyed the same
        // entry as username "a" with password "b|c" - one set of credentials silently answering for
        // another. Record equality compares field to field and cannot be confused that way.
        var key = new HttpClientKey(
            proxy.Type,
            proxy.Host,
            proxy.Port,
            proxy.Username,
            proxy.Password,
            isTracker,
            bindAddress,
            addressFamily,
            perServer);

        lock (_lock)
        {
            _disposal.ThrowIfDisposed(this);

            if (_clients.TryGetValue(key, out var existing))
            {
                existing.LastUsed = ++_useCounter;
                return existing.Client;
            }

            EvictUntilThereIsRoom();
            var client = CreateNewClient(proxy, isTracker, bindAddress, addressFamily, perServer);
            _clients[key] = new PooledClient(client) { LastUsed = ++_useCounter };
            return client;
        }
    }

    public void Dispose()
    {
        if (!_disposal.MarkDisposed())
        {
            return;
        }

        PooledClient[] clients;
        lock (_lock)
        {
            clients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var pooled in clients)
        {
            pooled.Client.Dispose();
        }
    }

    /// <summary>
    /// Drops the least recently requested configurations until a new one fits.
    ///
    /// <para>
    /// An evicted client is disposed, which fails any request still running on it. That is why the
    /// victim is the least recently <i>requested</i> entry and why the cap sits well above the number
    /// of configurations a normally configured engine uses: reaching it at all means the settings have
    /// been rewritten more times than there is room for, and the alternative - keeping every client a
    /// churning configuration ever produced - is the leak this replaced.
    /// </para>
    /// </summary>
    private void EvictUntilThereIsRoom()
    {
        while (_clients.Count >= MaxCachedClients)
        {
            var victim = _clients.MinBy(entry => entry.Value.LastUsed);
            _clients.Remove(victim.Key);
            victim.Value.Client.Dispose();
        }
    }

    private static HttpClient CreateNewClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress,
        AddressFamily? addressFamily,
        int maxConnectionsPerServer)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = maxConnectionsPerServer,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var effectiveFamily = bindAddress?.AddressFamily ?? addressFamily;
        if (effectiveFamily != null)
        {
            handler.ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, bindAddress, effectiveFamily.Value, cancellationToken);
        }

        if (proxy.Type != ProxyType.None && !string.IsNullOrEmpty(proxy.Host))
        {
            string proxyUri = proxy.Type switch
            {
                ProxyType.Socks5 => $"socks5://{proxy.Host}:{proxy.Port}",
                ProxyType.Http => $"http://{proxy.Host}:{proxy.Port}",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(proxyUri))
            {
                var webProxy = new WebProxy(proxyUri);
                if (!string.IsNullOrEmpty(proxy.Username))
                {
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                }
                handler.Proxy = webProxy;
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(isTracker ? 15 : 30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PeerSharp/{ProtocolConstants.ClientVersion}");

        return client;
    }

    /// <summary>
    /// Everything that makes two clients different. A record so equality is structural and exact:
    /// no separator to escape, and no field that can absorb another one's value.
    /// </summary>
    private readonly record struct HttpClientKey(
        ProxyType ProxyType,
        string? ProxyHost,
        int ProxyPort,
        string? ProxyUsername,
        string? ProxyPassword,
        bool IsTracker,
        IPAddress? BindAddress,
        AddressFamily? AddressFamily,
        int MaxConnectionsPerServer);

    /// <summary>A cached client and when it was last handed out, which sets eviction order.</summary>
    private sealed class PooledClient
    {
        public PooledClient(HttpClient client) => Client = client;

        public HttpClient Client { get; }

        public long LastUsed { get; set; }
    }

    private static ValueTask<Stream> ConnectAsync(
        DnsEndPoint remoteEndPoint,
        IPAddress? bindAddress,
        AddressFamily addressFamily,
        CancellationToken cancellationToken)
        => ConnectForTestingAsync(
            remoteEndPoint,
            bindAddress,
            addressFamily,
            static (host, ct) => Dns.GetHostAddressesAsync(host, ct),
            cancellationToken);

    /// <summary>
    /// Connects to the first address of the requested family that accepts, in the order the resolver
    /// returned them.
    ///
    /// <para>
    /// Choosing the address family means doing this by hand, and the thing not to lose while doing so
    /// is what the default connect path gives for free: it hands the socket the whole resolved set and
    /// walks it. A tracker published behind several A records has them precisely so that one host
    /// being down is survivable, and this path runs on every announce, so stopping at the first
    /// address turns an ordinary DNS arrangement into a failed announce.
    /// </para>
    ///
    /// <para>
    /// <c>resolveAddressesAsync</c> is a parameter rather than a direct <see cref="Dns"/> call so the
    /// walk itself can be tested without depending on what a real name happens to resolve to.
    /// </para>
    /// </summary>
    internal static async ValueTask<Stream> ConnectForTestingAsync(
        DnsEndPoint remoteEndPoint,
        IPAddress? bindAddress,
        AddressFamily addressFamily,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAddressesAsync,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(remoteEndPoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await resolveAddressesAsync(remoteEndPoint.Host, cancellationToken).ConfigureAwait(false);
        }

        // Every attempt spends from one ConnectTimeout, so a long list of dead records could burn the
        // whole budget before reaching a live address and leave the fallback buying nothing. Round
        // robin exists to spread load over a handful of hosts, not dozens, so a short walk keeps the
        // benefit while bounding the worst case.
        var candidates = addresses
            .Where(address => address.AddressFamily == addressFamily)
            .Take(MaxConnectAttempts)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        Exception? lastError = null;
        foreach (var remoteAddress in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (bindAddress != null)
                {
                    socket.Bind(new IPEndPoint(bindAddress, 0));
                }

                await socket.ConnectAsync(
                    new IPEndPoint(remoteAddress, remoteEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
