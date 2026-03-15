using System.Net;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// A lightweight HTTP server that streams from a TorrentStream.
/// Supports Range requests to enable seeking in media players.
/// </summary>
public sealed class HttpStreamServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ITorrent _torrent;
    private readonly int _fileIndex;
    private readonly ILogger<HttpStreamServer> _logger = TorrentLoggerFactory.CreateLogger<HttpStreamServer>();
    private readonly string _baseUrl;
    private AtomicDisposal _disposal = new();

    public HttpStreamServer(ITorrent torrent, int fileIndex)
    {
        _torrent = torrent;
        _fileIndex = fileIndex;
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
            if (context.Request.Url?.AbsolutePath != "/stream")
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
            long start = 0;
            long end = totalLength - 1;

            string? rangeHeader = context.Request.Headers["Range"];
            bool isPartial = false;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var rangeValue = rangeHeader.AsSpan("bytes=".Length);
                int dashIndex = rangeValue.IndexOf('-');
                if (dashIndex != -1)
                {
                    var startSpan = rangeValue.Slice(0, dashIndex);
                    var endSpan = rangeValue.Slice(dashIndex + 1);

                    if (long.TryParse(startSpan, out long rangeStart))
                    {
                        start = rangeStart;
                    }

                    if (endSpan.Length > 0 && long.TryParse(endSpan, out long rangeEnd))
                    {
                        end = rangeEnd;
                    }

                    isPartial = true;
                }
            }

            if (start < 0 || start >= totalLength || end < start || end >= totalLength)
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.AddHeader("Content-Range", $"bytes */{totalLength}");
                return;
            }

            long contentLength = end - start + 1;
            response.ProtocolVersion = new Version(1, 1);
            response.AddHeader("Accept-Ranges", "bytes");
            response.AddHeader("Connection", "keep-alive");
            response.ContentType = GetMimeType(fileInfo.Path);

            if (isPartial)
            {
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalLength}");
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.OK;
            }

            response.ContentLength64 = contentLength;

            if (context.Request.HttpMethod == "HEAD")
            {
                _logger.LogDebug("Serving HEAD request for {File}", fileInfo.Path);
                return;
            }

            _logger.LogDebug("Serving GET request for {File} range {Start}-{End} (Partial: {IsPartial})", fileInfo.Path, start, end, isPartial);

            await using var stream = await _torrent.OpenStreamAsync(_fileIndex).ConfigureAwait(false);
            stream.Seek(start, SeekOrigin.Begin);

            byte[] buffer = new byte[81920]; // 80KB buffer
            long bytesRemaining = contentLength;

            while (bytesRemaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                int read = await stream.ReadAsync(buffer, 0, toRead).ConfigureAwait(false);
                if (read == 0) break;

                try
                {
                    await response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    // Client disconnected
                    break;
                }

                bytesRemaining -= read;
            }
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

    private static string GetMimeType(string path)
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
                _listener.Stop();
                ((IDisposable)_listener).Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}
