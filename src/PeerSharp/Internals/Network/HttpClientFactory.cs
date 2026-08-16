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

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint remoteEndPoint,
        IPAddress? bindAddress,
        AddressFamily addressFamily,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(remoteEndPoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(remoteEndPoint.Host, cancellationToken).ConfigureAwait(false);
        }

        var remoteAddress = addresses.FirstOrDefault(address => address.AddressFamily == addressFamily)
            ?? throw new SocketException((int)SocketError.HostNotFound);

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
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
