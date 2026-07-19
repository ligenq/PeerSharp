using System.Collections.Concurrent;
using System.Net;

namespace PeerSharp.Internals.Network;

internal interface IHttpClientFactory
{
    HttpClient CreateClient(ProxySettings proxy, bool isTracker);
}

internal class HttpClientFactory : IHttpClientFactory
{
    private static readonly ConcurrentDictionary<string, HttpClient> Cache = new();

    public HttpClient CreateClient(ProxySettings proxy, bool isTracker)
    {
        // Cache key based on proxy settings and usage type (tracker vs web seed might have different timeouts/headers)
        string key = $"{proxy.Type}|{proxy.Host}|{proxy.Port}|{proxy.Username}|{proxy.Password}|{isTracker}";

        return Cache.GetOrAdd(key, _ => CreateNewClient(proxy, isTracker));
    }

    private static HttpClient CreateNewClient(ProxySettings proxy, bool isTracker)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

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
}
