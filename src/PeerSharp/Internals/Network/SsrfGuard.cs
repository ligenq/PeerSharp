using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Internals.Network;

/// <summary>What an HTTP request a torrent caused is for, which decides the shape it may take.</summary>
internal enum HttpRequestKind
{
    /// <summary>An announce or a scrape.</summary>
    Tracker,

    /// <summary>A range of a file from a BEP 19 web seed.</summary>
    WebSeed,

    /// <summary>
    /// A piece from a BEP 17 HTTP seed. The engine writes its query, so the shape of the URL the
    /// torrent gave is judged when the seed is added, as a <see cref="WebSeed"/>'s is, not here.
    /// </summary>
    HttpSeed,

    /// <summary>The .torrent a magnet link's <c>xs</c> parameter names.</summary>
    TorrentDownload,
}

/// <summary>The guard one HTTP client applies: how far it reaches, and to what kind of request.</summary>
/// <remarks>
/// The default is no guard, which is what a client for anything but a torrent's own URLs wants.
/// </remarks>
internal readonly record struct SsrfPolicy(SsrfMitigation Mode, HttpRequestKind Kind);

/// <summary>
/// A request refused because it would have reached a guarded address in a way the protocol has no use
/// for. A connection error to whoever sent it: retrying cannot succeed, but nor can any other cause.
/// </summary>
internal sealed class SsrfRefusedException(string message) : IOException(message);

/// <summary>
/// Keeps the URLs a torrent names from being used to reach services on this device or its network.
/// See <see cref="SsrfMitigation"/> for the rules; this is where they are applied.
/// </summary>
/// <remarks>
/// <para>
/// Two checks, because neither is enough alone. <see cref="CheckRequest"/> judges a URL by what it
/// says: one that names a guarded address outright must have the shape the protocol needs, and must
/// not have been reached by a redirect from elsewhere. <see cref="ScreenAddresses"/> judges a
/// connection by where it actually goes: a name that is not an outright guarded address may not
/// resolve to one.
/// </para>
/// <para>
/// Together they hold through connection pooling. A pooled connection to a guarded address exists only
/// for a host that named it outright, and every request to that host passes the URL check before it
/// can be sent on it.
/// </para>
/// </remarks>
internal static class SsrfGuard
{
    /// <summary>
    /// The URL a redirect chain began at, carried on the request for a caller that follows redirects
    /// itself rather than through the guarded client.
    /// </summary>
    public static readonly HttpRequestOptionsKey<Uri> RedirectedFrom = new("PeerSharp.SsrfGuard.RedirectedFrom");

    /// <summary>Whether connecting to an address needs a URL that names it outright.</summary>
    public static bool IsGuarded(IPAddress address, SsrfMitigation mode)
    {
        if (mode == SsrfMitigation.None)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0/8 is "this network". Connecting to 0.0.0.0 reaches this device on Linux and macOS.
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            if (bytes[0] == 0)
            {
                return true;
            }
        }

        return mode == SsrfMitigation.LocalNetworks && NetworkUtils.IsLocalAddress(address);
    }

    /// <summary>
    /// Whether a host - as a URL or a Host header carries it, with or without a port - names a guarded
    /// address outright: an address literal, or <c>localhost</c>, which resolves to loopback without
    /// asking DNS.
    /// </summary>
    public static bool IsExplicitlyGuarded(string host, SsrfMitigation mode)
    {
        if (mode == SsrfMitigation.None)
        {
            return false;
        }

        string name = HostName(host);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var address) && IsGuarded(address, mode);
    }

    /// <summary>
    /// Why a request may not be sent, or null when it may.
    /// </summary>
    /// <param name="policy">The guard to apply.</param>
    /// <param name="target">The URL being requested.</param>
    /// <param name="requestHost">
    /// The host the request names. Usually the URL's, but a caller that sends to an address it resolved
    /// itself names the real host in the Host header instead, and that is the one that counts.
    /// </param>
    /// <param name="redirectedFrom">The URL the redirect chain began at, if this is a redirect.</param>
    public static string? CheckRequest(SsrfPolicy policy, Uri target, string requestHost, Uri? redirectedFrom)
    {
        if (!IsExplicitlyGuarded(requestHost, policy.Mode))
        {
            return null;
        }

        if (redirectedFrom != null && !IsExplicitlyGuarded(redirectedFrom.Authority, policy.Mode))
        {
            return $"{redirectedFrom.Host} redirected to {target.Host}, which is on this device or its network";
        }

        if (policy.Kind == HttpRequestKind.HttpSeed)
        {
            return null;
        }

        if (policy.Kind == HttpRequestKind.Tracker)
        {
            // BEP 48 forms a scrape URL by replacing "announce" at the start of the last segment, so
            // "announce.php" and "scrape.php" are as legitimate as the bare words.
            string path = target.AbsolutePath;
            string lastSegment = path[(path.LastIndexOf('/') + 1)..];
            return lastSegment.StartsWith("announce", StringComparison.Ordinal) || lastSegment.StartsWith("scrape", StringComparison.Ordinal)
                ? null
                : $"a tracker on {target.Host} has to be at an announce or scrape path, not {path}";
        }

        return target.Query.Length > 1
            ? $"a request to {target.Host} cannot carry a query string"
            : null;
    }

    /// <summary>
    /// Narrows what a host resolved to down to the addresses a connection for it may use, or explains
    /// why there are none.
    /// </summary>
    /// <param name="mode">How far the guard reaches.</param>
    /// <param name="requestHost">The host the request names, as for <see cref="CheckRequest"/>.</param>
    /// <param name="addresses">What the connection's host resolved to.</param>
    /// <exception cref="SsrfRefusedException">Every address is guarded.</exception>
    public static IPAddress[] ScreenAddresses(SsrfMitigation mode, string requestHost, IPAddress[] addresses)
    {
        if (mode == SsrfMitigation.None || IsExplicitlyGuarded(requestHost, mode))
        {
            return addresses;
        }

        var allowed = Array.FindAll(addresses, address => !IsGuarded(address, mode));
        if (allowed.Length == 0 && addresses.Length > 0)
        {
            throw new SsrfRefusedException(
                $"SSRF mitigation: {HostName(requestHost)} resolves to {addresses[0]}, which is on this device or its network. " +
                "Name the address in the URL, or relax Connection.SsrfMitigation.");
        }

        return allowed;
    }

    /// <summary>The host a request names: its Host header when it carries one, otherwise its URL's.</summary>
    public static string RequestHost(HttpRequestMessage request) =>
        request.Headers.Host ?? request.RequestUri?.Authority ?? string.Empty;

    /// <summary>A host without its port or the brackets around an IPv6 literal.</summary>
    private static string HostName(string host)
    {
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            return close < 0 ? host : host[1..close];
        }

        int colon = host.IndexOf(':');
        return colon < 0 ? host : host[..colon];
    }
}

/// <summary>
/// Applies <see cref="SsrfGuard.CheckRequest"/> to every request a client sends, and follows redirects
/// itself so the check sees each one. The handler underneath must not follow them.
/// </summary>
internal sealed class SsrfGuardHandler : DelegatingHandler
{
    /// <summary>As many as HttpClient's own redirect handling follows.</summary>
    internal const int MaxRedirects = 50;

    private readonly SsrfPolicy _policy;
    private readonly bool _followRedirects;

    public SsrfGuardHandler(HttpMessageHandler innerHandler, SsrfPolicy policy, bool followRedirects)
        : base(innerHandler)
    {
        _policy = policy;
        _followRedirects = followRedirects;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = request.RequestUri ?? throw new InvalidOperationException("A request needs a URL.");
        request.Options.TryGetValue(SsrfGuard.RedirectedFrom, out Uri? origin);
        ThrowIfRefused(target, SsrfGuard.RequestHost(request), origin);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!_followRedirects)
        {
            return response;
        }

        origin ??= target;
        for (int redirects = 0; redirects < MaxRedirects && RedirectTarget(request.RequestUri, response) is { } location; redirects++)
        {
            // What HttpClient's own redirect handling does to a request it resends.
            bool toGet = response.StatusCode == HttpStatusCode.SeeOther && request.Method != HttpMethod.Head;
            response.Dispose();

            request.RequestUri = location;
            request.Headers.Host = null;
            request.Headers.Authorization = null;
            if (toGet)
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
            }

            ThrowIfRefused(location, location.Authority, origin);
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private void ThrowIfRefused(Uri target, string requestHost, Uri? redirectedFrom)
    {
        if (SsrfGuard.CheckRequest(_policy, target, requestHost, redirectedFrom) is { } reason)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"SSRF mitigation: {reason}.", new SsrfRefusedException(reason));
        }
    }

    /// <summary>
    /// Where a response redirects to, or null if it is not a redirect HttpClient would follow. Like
    /// HttpClient, an https request is never followed to plain http.
    /// </summary>
    private static Uri? RedirectTarget(Uri requested, HttpResponseMessage response)
    {
        bool isRedirect = response.StatusCode is HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        if (!isRedirect || response.Headers.Location is not { } location)
        {
            return null;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(requested, location);
        bool supported = target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps;
        bool downgrade = requested.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps;
        return supported && !downgrade ? target : null;
    }
}
