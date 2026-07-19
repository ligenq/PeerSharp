using System.Buffers;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// A lightweight HTTP server that streams from a TorrentStream.
/// Supports Range requests to enable seeking in media players.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class HttpStreamServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly HttpStreamRequestHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<HttpStreamServer> _logger = TorrentLoggerFactory.CreateLogger<HttpStreamServer>();
    private readonly string _baseUrl;
    private AtomicDisposal _disposal = new();

    public HttpStreamServer(ITorrent torrent, int fileIndex)
    {
        _handler = new HttpStreamRequestHandler(torrent, fileIndex);
        _listener = new HttpListener();

        // Find an available port
        int port = GetAvailablePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_baseUrl);
    }

    public string Url => $"{_baseUrl}stream";

    public void Start()
    {
        _listener.Start();
        _ = AcceptConnectionsAsync(); // Fire-and-forget OK: method handles exceptions internally
        _logger.LogInformation("HTTP Stream Server started at {Url}", Url);
    }

    private async Task AcceptConnectionsAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = ProcessRequestAsync(context);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // Normal during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting HTTP connections");
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        using var response = context.Response;
        try
        {
            await _handler.ProcessAsync(
                new HttpListenerStreamRequest(context.Request),
                new HttpListenerStreamResponse(response),
                _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server is shutting down — abandon this request quietly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing HTTP stream request");
            try
            {
                if (response.OutputStream.CanWrite)
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
            }
            catch
            {
                // Ignore errors setting status code if headers already sent
            }
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposal.MarkDisposed())
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
                // Ignore — token source may already be disposed under heavy contention.
            }

            try
            {
                _listener.Stop();
                ((IDisposable)_listener).Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }

            _cts.Dispose();
        }
    }
}

internal sealed class HttpStreamRequestHandler
{
    private const int BufferSize = 81920;
    private readonly ITorrent _torrent;
    private readonly int _fileIndex;
    private readonly ILogger<HttpStreamRequestHandler> _logger = TorrentLoggerFactory.CreateLogger<HttpStreamRequestHandler>();

    public HttpStreamRequestHandler(ITorrent torrent, int fileIndex)
    {
        _torrent = torrent;
        _fileIndex = fileIndex;
    }

    public async Task ProcessAsync(IHttpStreamRequest request, IHttpStreamResponse response, CancellationToken cancellationToken = default)
    {
        if (request.Path != "/stream")
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var fileInfo = _torrent.GetAllFileInfo().ElementAtOrDefault(_fileIndex);
        if (fileInfo == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        long totalLength = fileInfo.Size;
        var range = HttpRangeParser.Parse(request.RangeHeader, totalLength);
        if (!range.IsValid)
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{totalLength}");
            return;
        }

        long contentLength = range.End - range.Start + 1;
        response.ProtocolVersion = new Version(1, 1);
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Connection", "keep-alive");
        response.ContentType = HttpStreamMimeTypes.GetMimeType(fileInfo.Path);
        response.ContentLength = contentLength;

        if (range.IsPartial)
        {
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.AddHeader("Content-Range", $"bytes {range.Start}-{range.End}/{totalLength}");
        }
        else
        {
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        if (request.Method == "HEAD")
        {
            _logger.LogDebug("Serving HEAD request for {File}", fileInfo.Path);
            return;
        }

        _logger.LogDebug("Serving GET request for {File} range {Start}-{End} (Partial: {IsPartial})", fileInfo.Path, range.Start, range.End, range.IsPartial);

        await using var stream = await _torrent.OpenStreamAsync(_fileIndex, cancellationToken).ConfigureAwait(false);
        stream.Seek(range.Start, SeekOrigin.Begin);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long bytesRemaining = contentLength;

            while (bytesRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    // Client disconnected while streaming; stop this response without failing the server.
                    break;
                }

                bytesRemaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal interface IHttpStreamRequest
{
    string Method { get; }
    string Path { get; }
    string? RangeHeader { get; }
}

internal interface IHttpStreamResponse
{
    Stream Body { get; }
    long ContentLength { get; set; }
    string ContentType { get; set; }
    Version ProtocolVersion { get; set; }
    int StatusCode { get; set; }
    void AddHeader(string name, string value);
}

internal readonly record struct HttpByteRange(bool IsValid, bool IsPartial, long Start, long End);

internal static class HttpRangeParser
{
    public static HttpByteRange Parse(string? rangeHeader, long totalLength)
    {
        // No range header (or different unit): caller serves the whole file as a 200.
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
        {
            bool wholeFileValid = totalLength > 0;
            return new HttpByteRange(wholeFileValid, IsPartial: false, Start: 0, End: totalLength - 1);
        }

        var rangeValue = rangeHeader.AsSpan("bytes=".Length);

        // RFC 7233 multi-range (e.g. "bytes=0-5,10-15") is not supported here. Treat any range
        // header that contains a comma as malformed so we return 416 instead of silently serving
        // the first range or the whole file.
        if (rangeValue.IndexOf(',') >= 0)
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
        }

        int dashIndex = rangeValue.IndexOf('-');
        if (dashIndex < 0)
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
        }

        var startSpan = rangeValue[..dashIndex];
        var endSpan = rangeValue[(dashIndex + 1)..];

        // RFC 7233 §2.1 suffix-byte-range-spec: "bytes=-N" means the last N bytes.
        if (startSpan.IsEmpty)
        {
            if (!long.TryParse(endSpan, out long suffix) || suffix <= 0)
            {
                return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
            }

            long suffixStart = Math.Max(0, totalLength - suffix);
            bool suffixValid = totalLength > 0;
            return new HttpByteRange(suffixValid, IsPartial: true, suffixStart, totalLength - 1);
        }

        if (!long.TryParse(startSpan, out long rangeStart))
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
        }

        long rangeEnd = totalLength - 1;
        if (!endSpan.IsEmpty && !long.TryParse(endSpan, out rangeEnd))
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: rangeStart, End: totalLength - 1);
        }

        // Open-ended high (bytes=N-) is allowed and resolves to N..totalLength-1.
        bool valid = totalLength > 0 && rangeStart >= 0 && rangeStart < totalLength && rangeEnd >= rangeStart && rangeEnd < totalLength;
        return new HttpByteRange(valid, IsPartial: true, rangeStart, rangeEnd);
    }
}

internal static class HttpStreamMimeTypes
{
    public static string GetMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }
}

[ExcludeFromCodeCoverage]
internal sealed class HttpListenerStreamRequest : IHttpStreamRequest
{
    private readonly HttpListenerRequest _request;

    public HttpListenerStreamRequest(HttpListenerRequest request)
    {
        _request = request;
    }

    public string Method => _request.HttpMethod;
    public string Path => _request.Url?.AbsolutePath ?? string.Empty;
    public string? RangeHeader => _request.Headers["Range"];
}

[ExcludeFromCodeCoverage]
internal sealed class HttpListenerStreamResponse : IHttpStreamResponse
{
    private readonly HttpListenerResponse _response;

    public HttpListenerStreamResponse(HttpListenerResponse response)
    {
        _response = response;
    }

    public Stream Body => _response.OutputStream;

    public long ContentLength
    {
        get => _response.ContentLength64;
        set => _response.ContentLength64 = value;
    }

    public string ContentType
    {
        get => _response.ContentType ?? string.Empty;
        set => _response.ContentType = value;
    }

    public Version ProtocolVersion
    {
        get => _response.ProtocolVersion;
        set => _response.ProtocolVersion = value;
    }

    public int StatusCode
    {
        get => _response.StatusCode;
        set => _response.StatusCode = value;
    }

    public void AddHeader(string name, string value)
    {
        _response.AddHeader(name, value);
    }
}
