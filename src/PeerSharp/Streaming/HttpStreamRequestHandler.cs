using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.Streaming;

/// <summary>
/// Answers one HTTP request for one file of a torrent, independent of how the request arrived.
/// </summary>
internal sealed class HttpStreamRequestHandler
{
    /// <summary>A read of the torrent this slow is logged: long enough to matter to a player.</summary>
    private static readonly TimeSpan SlowStep = TimeSpan.FromMilliseconds(500);

    /// <summary>The path a server with no access token answers on.</summary>
    public const string DefaultPath = "/stream";

    private const int BufferSize = 81920;
    private const string AllowedMethods = "GET, HEAD, OPTIONS";

    private readonly ITorrent _torrent;
    private readonly int _fileIndex;
    private readonly byte[] _path;

    /// <summary>Where side files live: the stream's path up to and including its last slash.</summary>
    private readonly byte[] _directory;

    private readonly ConcurrentDictionary<string, SideFile> _files = new(StringComparer.Ordinal);
    private readonly ILogger<HttpStreamRequestHandler> _logger;

    public HttpStreamRequestHandler(ITorrent torrent, int fileIndex)
        : this(torrent, fileIndex, DefaultPath, NullLoggerFactory.Instance)
    {
    }

    internal HttpStreamRequestHandler(ITorrent torrent, int fileIndex, string path, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<HttpStreamRequestHandler>();
        _torrent = torrent;
        _fileIndex = fileIndex;
        _path = Encoding.UTF8.GetBytes(path);
        _directory = Encoding.UTF8.GetBytes(path[..(path.LastIndexOf('/') + 1)]);
    }

    /// <summary>
    /// Serves <paramref name="content"/> as <paramref name="name"/>, beside the stream, replacing a file
    /// of that name. The content is asked for on every request, so it can change between them.
    /// </summary>
    public void AddFile(string name, string contentType, Func<ReadOnlyMemory<byte>> content) =>
        _files[name] = new SideFile(contentType, content);

    public async Task ProcessAsync(IHttpStreamRequest request, IHttpStreamResponse response, CancellationToken cancellationToken = default)
    {
        SideFile? sideFile = null;
        if (!IsTheStreamPath(request.Path) && !TryFindFile(request.Path, out sideFile))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // A cast receiver is a web page, and fetching from another origin - which this server always
        // is to it - needs the answer to say so. The URL carries the access token, so allowing any
        // origin gives nothing to a page that does not already hold the URL.
        response.AddHeader("Access-Control-Allow-Origin", "*");

        if (request.Method == "OPTIONS")
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.AddHeader("Access-Control-Allow-Methods", AllowedMethods);
            response.AddHeader("Access-Control-Allow-Headers", "Range");
            response.AddHeader("Access-Control-Max-Age", "86400");
            return;
        }

        if (request.Method is not ("GET" or "HEAD"))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.AddHeader("Allow", AllowedMethods);
            return;
        }

        if (sideFile is not null)
        {
            await ServeSideFileAsync(sideFile, request, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fileInfo = _torrent.GetAllFileInfo().ElementAtOrDefault(_fileIndex);
        if (fileInfo == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        long totalLength = fileInfo.Size;
        var range = HttpRangeParser.Parse(request.Method == "GET" ? request.RangeHeader : null, totalLength);
        if (!range.IsValid)
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{totalLength}");
            return;
        }

        long contentLength = range.End - range.Start + 1;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Access-Control-Expose-Headers", "Accept-Ranges, Content-Length, Content-Range");
        response.ContentType = StreamMediaTypes.GetMimeType(fileInfo.Path);
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

        if (contentLength == 0) return;
        await using var stream = await _torrent.OpenStreamAsync(_fileIndex, cancellationToken).ConfigureAwait(false);
        stream.Seek(range.Start, SeekOrigin.Begin);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan? firstByte = null;
        long bytesRemaining = contentLength;
        var longestRead = TimeSpan.Zero;
        var longestWrite = TimeSpan.Zero;
        long lastSent = started;
        try
        {
            while (bytesRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                long readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                var readTook = System.Diagnostics.Stopwatch.GetElapsedTime(readStarted);
                firstByte ??= System.Diagnostics.Stopwatch.GetElapsedTime(started);
                longestRead = readTook > longestRead ? readTook : longestRead;
                if (readTook >= SlowStep)
                {
                    // The torrent kept the player waiting: what it waits on, and for how long.
                    _logger.LogDebug("Range {Start}-{End}: waited {Ms}ms for data at {Offset}",
                        range.Start, range.End, (int)readTook.TotalMilliseconds, range.Start + contentLength - bytesRemaining);
                }
                if (read == 0)
                {
                    // Content-Length was already announced from the file size, so a short read
                    // here means the file is not what the metadata claims. Fail loudly instead
                    // of completing the response with a truncated body.
                    throw new EndOfStreamException(
                        $"Stream for '{fileInfo.Path}' ended {bytesRemaining} bytes short of the announced range.");
                }

                // A client that has gone away surfaces here as an IOException, which the server
                // treats as the end of the connection rather than as a failure.
                long writeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                var writeTook = System.Diagnostics.Stopwatch.GetElapsedTime(writeStarted);
                longestWrite = writeTook > longestWrite ? writeTook : longestWrite;
                lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
                bytesRemaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // How long the player waited for this range, and how much of it it took before moving on;
            // the longest the torrent kept a read waiting, the longest the player left a write unread,
            // and how long nothing had been sent when the range ended.
            _logger.LogDebug(
                "Range {Start}-{End}: first byte after {FirstByteMs}ms, {Sent} bytes sent in {Ms}ms; longest read {ReadMs}ms, longest write {WriteMs}ms, last byte {SilentMs}ms before the end",
                range.Start,
                range.End,
                (int?)firstByte?.TotalMilliseconds,
                contentLength - bytesRemaining,
                (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                (int)longestRead.TotalMilliseconds,
                (int)longestWrite.TotalMilliseconds,
                (int)System.Diagnostics.Stopwatch.GetElapsedTime(lastSent).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Serves a side file whole. They are small - subtitles, artwork - so ranges are not offered, and
    /// they are not to be cached: one that is still being written is fetched again for its latest.
    /// </summary>
    private static async Task ServeSideFileAsync(SideFile file, IHttpStreamRequest request, IHttpStreamResponse response, CancellationToken cancellationToken)
    {
        var content = file.Content();
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = file.ContentType;
        response.ContentLength = content.Length;
        response.AddHeader("Cache-Control", "no-store");

        if (request.Method == "GET")
        {
            await response.Body.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the side file a request names. The directory part carries the access token, so it is
    /// compared in constant time, as the stream's path is; the name after it is no secret.
    /// </summary>
    private bool TryFindFile(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SideFile? file)
    {
        file = null;
        var candidate = Encoding.UTF8.GetBytes(path);
        return !_files.IsEmpty
            && candidate.Length > _directory.Length
            && CryptographicOperations.FixedTimeEquals(candidate.AsSpan(0, _directory.Length), _directory)
            && _files.TryGetValue(path[_directory.Length..], out file);
    }

    /// <summary>
    /// Whether a request is for the served file. Compared in constant time, since the path can carry
    /// the access token and an early-out comparison would reveal how much of a guess was right.
    /// </summary>
    private bool IsTheStreamPath(string path)
    {
        var candidate = Encoding.UTF8.GetBytes(path);
        return candidate.Length == _path.Length && CryptographicOperations.FixedTimeEquals(candidate, _path);
    }
}

/// <summary>A small file served beside the stream, produced afresh for every request.</summary>
internal sealed record SideFile(string ContentType, Func<ReadOnlyMemory<byte>> Content);

internal interface IHttpStreamRequest
{
    string Method { get; }
    string Path { get; }
    string? RangeHeader { get; }
}

internal interface IHttpStreamResponse
{
    /// <summary>The response body. The status and headers are sent before its first byte.</summary>
    Stream Body { get; }
    long ContentLength { get; set; }
    string ContentType { get; set; }
    int StatusCode { get; set; }
    void AddHeader(string name, string value);
}

internal readonly record struct HttpByteRange(bool IsValid, bool IsPartial, long Start, long End);

internal static class HttpRangeParser
{
    public static HttpByteRange Parse(string? rangeHeader, long totalLength)
    {
        // No range header (or different unit): caller serves the whole file as a 200.
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            bool wholeFileValid = totalLength >= 0;
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
            if (!long.TryParse(endSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
            {
                return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
            }

            long suffixStart = Math.Max(0, totalLength - suffix);
            bool suffixValid = totalLength > 0;
            return new HttpByteRange(suffixValid, IsPartial: true, suffixStart, totalLength - 1);
        }

        if (!long.TryParse(startSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long rangeStart))
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: 0, End: totalLength - 1);
        }

        long rangeEnd = totalLength - 1;
        bool endPresent = !endSpan.IsEmpty;
        if (endPresent && !long.TryParse(endSpan, NumberStyles.None, CultureInfo.InvariantCulture, out rangeEnd))
        {
            return new HttpByteRange(IsValid: false, IsPartial: true, Start: rangeStart, End: totalLength - 1);
        }

        // Open-ended high (bytes=N-) is allowed and resolves to N..totalLength-1. What decides
        // satisfiability is the first byte position alone: RFC 7233 §2.1 makes a range unsatisfiable
        // when it starts at or past the end, and a last-byte-pos that precedes it is malformed.
        bool valid = totalLength > 0
            && rangeStart >= 0
            && rangeStart < totalLength
            && (!endPresent || rangeEnd >= rangeStart);

        // An end at or past the end of the file is not a rejection - "the byte range is interpreted
        // as the remainder of the representation". Players request fixed-size chunks, so the last
        // chunk of every file asks for more than is left, as does every chunk of a file smaller than
        // one chunk. Answering those with 416 fails playback at the end of each file.
        if (valid && rangeEnd >= totalLength)
        {
            rangeEnd = totalLength - 1;
        }

        return new HttpByteRange(valid, IsPartial: true, rangeStart, rangeEnd);
    }
}
