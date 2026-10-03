using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Core;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// A lightweight HTTP server that streams one file of a torrent while it downloads. Supports range
/// requests, so media players can seek.
/// </summary>
/// <remarks>
/// <para>
/// Built directly on a socket rather than on <see cref="HttpListener"/>. On Windows, HttpListener
/// goes through http.sys, which refuses any address but loopback to a process without an
/// administrator-granted URL reservation - so a server a Chromecast or a TV could reach was not
/// possible there. A socket binds wherever it is told, on every platform.
/// </para>
/// <para>
/// By default the server listens on loopback, for a player on the same device. To stream to another
/// device, bind it to this device's address on the shared network with
/// <see cref="HttpStreamServerOptions.BindAddress"/>; its <see cref="Url"/> then carries a secret
/// token, so the file is served only to whoever was handed the URL.
/// </para>
/// </remarks>
public sealed class HttpStreamServer : IDisposable
{
    /// <summary>
    /// The most connections served at once. A player uses a handful; anything beyond this is refused
    /// rather than allowed to hold streams, and the pieces they prioritise, without limit.
    /// </summary>
    private const int MaxConcurrentConnections = 32;

    private readonly Socket _listener;
    private readonly HttpStreamRequestHandler _handler;
    private readonly ITorrent _torrent;
    private readonly int _fileIndex;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The stopping token, taken once and held. Requests already being served when the server shuts
    /// down would otherwise read <c>_cts.Token</c> after it was disposed and fault on the way out.
    /// </summary>
    private readonly CancellationToken _stoppingToken;
    private readonly ILogger<HttpStreamServer> _logger;
    private int _connections;
    private int _started;
    private AtomicDisposal _disposal = new();

    /// <summary>
    /// Initializes a server that streams one file from <paramref name="torrent"/>, binding to an
    /// available loopback port.
    /// </summary>
    /// <param name="torrent">The torrent to stream from.</param>
    /// <param name="fileIndex">Index of the file within the torrent.</param>
    public HttpStreamServer(ITorrent torrent, int fileIndex)
        : this(torrent, fileIndex, new HttpStreamServerOptions(), NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Initializes a server that streams one file from <paramref name="torrent"/>, binding to an
    /// available loopback port.
    /// </summary>
    /// <param name="torrent">The torrent to stream from.</param>
    /// <param name="fileIndex">Index of the file within the torrent.</param>
    /// <param name="loggerFactory">Factory used to create the server's logger.</param>
    public HttpStreamServer(ITorrent torrent, int fileIndex, ILoggerFactory loggerFactory)
        : this(torrent, fileIndex, new HttpStreamServerOptions(), loggerFactory)
    {
    }

    /// <summary>
    /// Initializes a server that streams one file from <paramref name="torrent"/>, listening where
    /// <paramref name="options"/> says.
    /// </summary>
    /// <param name="torrent">The torrent to stream from.</param>
    /// <param name="fileIndex">Index of the file within the torrent.</param>
    /// <param name="options">The address, port and access token to use.</param>
    /// <exception cref="ArgumentException">
    /// The bind address is a wildcard address, or the access token is not URL-safe.
    /// </exception>
    /// <exception cref="SocketException">The address or port cannot be bound.</exception>
    public HttpStreamServer(ITorrent torrent, int fileIndex, HttpStreamServerOptions options)
        : this(torrent, fileIndex, options, NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Initializes a server that streams one file from <paramref name="torrent"/>, listening where
    /// <paramref name="options"/> says.
    /// </summary>
    /// <param name="torrent">The torrent to stream from.</param>
    /// <param name="fileIndex">Index of the file within the torrent.</param>
    /// <param name="options">The address, port and access token to use.</param>
    /// <param name="loggerFactory">Factory used to create the server's logger.</param>
    /// <exception cref="ArgumentException">
    /// The bind address is a wildcard address, or the access token is not URL-safe.
    /// </exception>
    /// <exception cref="SocketException">The address or port cannot be bound.</exception>
    public HttpStreamServer(ITorrent torrent, int fileIndex, HttpStreamServerOptions options, ILoggerFactory loggerFactory)
        : this(torrent, fileIndex, options, loggerFactory, TimeProvider.System)
    {
    }

    [AllowHeavyConstructor(
        "Binding is a local call with no network traffic, and it is what lets Url be known - with the " +
        "port held - before Start, which callers rely on to hand a player the URL straight away.")]
    internal HttpStreamServer(
        ITorrent torrent,
        int fileIndex,
        HttpStreamServerOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var address = options.BindAddress ?? throw new ArgumentException("A bind address is required.", nameof(options));
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            throw new ArgumentException(
                "Bind to the specific address a player will use. A URL naming every interface names none of them.",
                nameof(options));
        }

        if (options.Port is < 0 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Port, "The port must be between 0 and 65535.");
        }

        string? token = options.AccessToken ?? (IPAddress.IsLoopback(address) ? null : NewAccessToken());
        if (token != null && !IsUrlSafe(token))
        {
            throw new ArgumentException("An access token may contain only letters, digits, '-' and '_'.", nameof(options));
        }

        string path = token == null
            ? HttpStreamRequestHandler.DefaultPath
            : $"/{token}{HttpStreamRequestHandler.DefaultPath}";

        _torrent = torrent;
        _fileIndex = fileIndex;
        _timeProvider = timeProvider;
        _stoppingToken = _cts.Token;
        _logger = loggerFactory.CreateLogger<HttpStreamServer>();
        _handler = new HttpStreamRequestHandler(torrent, fileIndex, path, loggerFactory);

        // Bound now, listened on at Start, so the URL is known - and the port held - before any
        // player is handed it. Probing for a free port and binding later could lose it in between.
        _listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            _listener.Bind(new IPEndPoint(address, options.Port));
        }
        catch
        {
            _listener.Dispose();
            _cts.Dispose();
            throw;
        }

        // Plain HTTP by necessity: a cast receiver or TV cannot validate a certificate for an address
        // on a home network. The access token in the path is what keeps other devices out.
#pragma warning disable S5332
        Url = $"http://{Authority((IPEndPoint)_listener.LocalEndPoint!)}{path}";
#pragma warning restore S5332
    }

    /// <summary>
    /// Gets the URL a media player should open. Available before <see cref="Start"/>.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Serves a small file beside the stream - subtitles for a cast receiver, say - and returns its URL,
    /// which carries the same access token as <see cref="Url"/>. Adding a name again replaces the file.
    /// </summary>
    /// <param name="name">
    /// The file's name in the URL: letters, digits, '-', '_' and '.'. It cannot be the stream's own name.
    /// </param>
    /// <param name="contentType">The MIME type it is served as, such as <c>text/vtt</c>.</param>
    /// <param name="content">
    /// Produces the file's bytes. Asked on every request, and the answer is marked as not to be cached,
    /// so a file that grows - subtitles extracted as a film downloads - is fetched again at its latest.
    /// </param>
    /// <exception cref="ArgumentException">The name is empty, not URL-safe, or the stream's own.</exception>
    public string AddFile(string name, string contentType, Func<ReadOnlyMemory<byte>> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(content);
        _disposal.ThrowIfDisposed(this);
        if (contentType.Any(c => c < ' ' || c == 0x7F))
        {
            throw new ArgumentException("A content type cannot contain control characters.", nameof(contentType));
        }
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || name.All(c => c == '.')
            || $"/{name}" == HttpStreamRequestHandler.DefaultPath)
        {
            throw new ArgumentException("A file name may contain only letters, digits, '-', '_' and '.', and cannot be the stream's.", nameof(name));
        }

        _handler.AddFile(name, contentType, content);
        return Url[..(Url.LastIndexOf('/') + 1)] + name;
    }

    /// <summary>
    /// Gets the MIME type the file is served as, judged by its name - what a cast sender puts in its
    /// load request. <c>application/octet-stream</c> until the torrent's metadata is known, and for
    /// anything that is not a recognised media type.
    /// </summary>
    public string ContentType =>
        _torrent.GetAllFileInfo().ElementAtOrDefault(_fileIndex) is { } file
            ? StreamMediaTypes.GetMimeType(file.Path)
            : StreamMediaTypes.Fallback;

    /// <summary>
    /// Begins accepting requests. Returns as soon as the listener is listening; connections are
    /// served in the background.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The server has been disposed.</exception>
    public void Start()
    {
        _disposal.ThrowIfDisposed(this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("The server has already been started.");
        }

        _listener.Listen();
        _ = AcceptConnectionsAsync(); // Fire-and-forget OK: method handles exceptions internally
        _logger.LogInformation("HTTP Stream Server started at {Url}", Url);
    }

    /// <summary>
    /// Stops listening, ends every connection being served, and releases the listener. Safe to call
    /// more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException ex)
            {
                // A registration on the stopping token threw. Shutdown carries on regardless.
                _logger.LogDebug(ex, "A stopping callback failed during shutdown");
            }

            _listener.Dispose();
            _cts.Dispose();
        }
    }

    private async Task AcceptConnectionsAsync()
    {
        try
        {
            while (!_stoppingToken.IsCancellationRequested)
            {
                var socket = await _listener.AcceptAsync(_stoppingToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _connections) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    _logger.LogWarning("Refusing a streaming connection: {Max} are already open", MaxConcurrentConnections);
                    socket.Dispose();
                    continue;
                }

                _ = ServeConnectionAsync(socket); // Fire-and-forget OK: method handles exceptions internally
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException && _stoppingToken.IsCancellationRequested)
        {
            // Normal during shutdown: disposing the listener ends a pending accept this way.
            _logger.LogDebug(ex, "Stopped accepting streaming connections");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting HTTP connections");
        }
    }

    private async Task ServeConnectionAsync(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
            // A client that resets the connection, or goes idle past the keep-alive timeout, ends its read
            // as end of input rather than with an exception - players drop connections on every seek.
            var transport = new Internals.Network.SocketStream(socket);
            await using (transport.ConfigureAwait(false))
            {
                var connection = new HttpStreamConnection(transport, _handler, _timeProvider, _logger);
                await connection.ServeAsync(_stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Server is shutting down - abandon this connection quietly. Closing it part way through a
            // response is what tells the client the body is incomplete.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The client went away, which is how most streaming connections end: a player seeking
            // abandons the request it was reading.
            _logger.LogDebug(ex, "Streaming client disconnected");
        }
        catch (TimeoutException ex)
        {
            // The swarm stopped supplying the pieces this range needs after the response had
            // started. Content-Length has been sent, so the only honest signal left is a closed
            // connection.
            _logger.LogWarning(ex, "Timed out waiting for torrent data while streaming");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving HTTP stream connection");
        }
        finally
        {
            socket.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }

    private static string NewAccessToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static bool IsUrlSafe(string token) =>
        token.Length > 0 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The host and port of an endpoint, written as a URL needs them.</summary>
    private static string Authority(IPEndPoint endpoint)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return endpoint.ToString();
        }

        // An IPv6 literal goes in brackets, and a zone index's '%' has to be escaped (RFC 6874).
        var host = endpoint.Address.ToString().Replace("%", "%25", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture, $"[{host}]:{endpoint.Port}");
    }
}
