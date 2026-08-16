using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Network;

internal interface IHttpClientFactory
{
    HttpClient CreateClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress = null,
        AddressFamily? addressFamily = null);
}

internal class HttpClientFactory : IHttpClientFactory
{
    private static readonly ConcurrentDictionary<string, HttpClient> Cache = new();

    /// <summary>How many resolved addresses one connect may try before giving up.</summary>
    private const int MaxConnectAttempts = 3;

    public HttpClient CreateClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress = null,
        AddressFamily? addressFamily = null)
    {
        if (bindAddress != null && addressFamily != null && bindAddress.AddressFamily != addressFamily)
        {
            throw new ArgumentException("The requested address family must match the bind address.", nameof(addressFamily));
        }

        // Cache key based on proxy settings and usage type (tracker vs web seed might have different timeouts/headers)
        string key = $"{proxy.Type}|{proxy.Host}|{proxy.Port}|{proxy.Username}|{proxy.Password}|{isTracker}|{bindAddress}|{addressFamily}";

        return Cache.GetOrAdd(key, _ => CreateNewClient(proxy, isTracker, bindAddress, addressFamily));
    }

    private static HttpClient CreateNewClient(
        ProxySettings proxy,
        bool isTracker,
        IPAddress? bindAddress,
        AddressFamily? addressFamily)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
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
