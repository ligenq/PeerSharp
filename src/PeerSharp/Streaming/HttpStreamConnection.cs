using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PeerSharp.Core;

namespace PeerSharp.Streaming;

/// <summary>
/// Serves the requests arriving on one client connection, one after another, for as long as the
/// client keeps the connection open.
/// </summary>
/// <remarks>
/// <para>
/// HTTP/1.1 with keep-alive, because media players and cast receivers issue a stream of range
/// requests - seeking is a new request - and opening a connection for each would add a round trip
/// to every seek.
/// </para>
/// <para>
/// The status line and headers are written as late as possible: just before the first byte of the
/// body, or when the handler returns without one. Until then a failure can still be answered
/// honestly - a swarm that never supplies the first piece becomes a 503 rather than a connection
/// that closes after announcing a length it never delivers.
/// </para>
/// </remarks>
internal sealed class HttpStreamConnection
{
    /// <summary>The largest request head accepted. Players send a few hundred bytes.</summary>
    internal const int MaxHeadBytes = 16 * 1024;

    /// <summary>
    /// How long a connection may sit between requests, or take to deliver one request head, before
    /// it is closed.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private static ReadOnlySpan<byte> HeadTerminator => "\r\n\r\n"u8;

    private readonly Stream _transport;
    private readonly HttpStreamRequestHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly byte[] _buffer = new byte[MaxHeadBytes];
    private int _buffered;

    public HttpStreamConnection(Stream transport, HttpStreamRequestHandler handler, TimeProvider timeProvider, ILogger logger)
    {
        _transport = transport;
        _handler = handler;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Serves requests until the client closes the connection, asks for it to be closed, goes idle,
    /// or sends something that cannot be answered on it.
    /// </summary>
    /// <remarks>
    /// An exception escapes only once the response has started, when nothing but closing the
    /// connection can still tell the client its response is incomplete.
    /// </remarks>
    public async Task ServeAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var (outcome, request) = await ReadRequestHeadAsync(stoppingToken).ConfigureAwait(false);
            switch (outcome)
            {
                case HeadOutcome.Closed:
                    return;

                case HeadOutcome.Malformed:
                    await AnswerAndCloseAsync(HttpStatusCode.BadRequest, stoppingToken).ConfigureAwait(false);
                    return;

                case HeadOutcome.TooLarge:
                    await AnswerAndCloseAsync(HttpStatusCode.RequestHeaderFieldsTooLarge, stoppingToken).ConfigureAwait(false);
                    return;
            }

            // A request with a body is answered and the connection then closed: the server reads no
            // bodies, so it cannot tell where the next request would begin.
            bool keepAlive = request!.WantsKeepAlive && !request.HasBody;
            var response = new Response(_transport, keepAlive);

            try
            {
                await _handler.ProcessAsync(request, response, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !response.HeadersSent)
            {
                // Nothing has been sent yet, so the client can still be told plainly.
                var status = ex is TimeoutException ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError;
                _logger.LogWarning(ex, "Answering {Method} {Path} with {Status}", request.Method, request.Path, (int)status);
                response.Reset(status);
                await response.CompleteAsync(stoppingToken).ConfigureAwait(false);
                return;
            }

            await response.CompleteAsync(stoppingToken).ConfigureAwait(false);
            if (!keepAlive)
            {
                return;
            }
        }
    }

    private async Task AnswerAndCloseAsync(HttpStatusCode status, CancellationToken cancellationToken)
    {
        var response = new Response(_transport, keepAlive: false);
        response.Reset(status);
        await response.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(HeadOutcome Outcome, HttpRequestHead? Request)> ReadRequestHeadAsync(CancellationToken stoppingToken)
    {
        using var idle = new CancellationTokenSource(IdleTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(idle.Token, stoppingToken);

        while (true)
        {
            DiscardLeadingEmptyLines();

            int end = _buffer.AsSpan(0, _buffered).IndexOf(HeadTerminator);
            if (end >= 0)
            {
                var request = HttpRequestHead.TryParse(_buffer.AsSpan(0, end));
                Consume(end + HeadTerminator.Length);
                return request == null ? (HeadOutcome.Malformed, null) : (HeadOutcome.Request, request);
            }

            if (_buffered == _buffer.Length)
            {
                return (HeadOutcome.TooLarge, null);
            }

            int read;
            try
            {
                read = await _transport.ReadAsync(_buffer.AsMemory(_buffered), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Idle for too long: an ordinary end to a kept-alive connection, not a failure.
                return (HeadOutcome.Closed, null);
            }

            if (read == 0)
            {
                // The client closed the connection - between requests, or part way through a head
                // there is now nobody to answer.
                return (HeadOutcome.Closed, null);
            }

            _buffered += read;
        }
    }

    /// <summary>
    /// RFC 9112 2.2: a server should ignore empty lines received before a request line, which some
    /// clients send after a request's body.
    /// </summary>
    private void DiscardLeadingEmptyLines()
    {
        int start = 0;
        while (_buffered - start >= 2 && _buffer[start] == '\r' && _buffer[start + 1] == '\n')
        {
            start += 2;
        }

        Consume(start);
    }

    private void Consume(int count)
    {
        if (count == 0)
        {
            return;
        }

        Buffer.BlockCopy(_buffer, count, _buffer, 0, _buffered - count);
        _buffered -= count;
    }

    private enum HeadOutcome
    {
        Request,
        Closed,
        Malformed,
        TooLarge,
    }

    /// <summary>A response written straight to the connection, head first.</summary>
    private sealed class Response : IHttpStreamResponse
    {
        private readonly Stream _transport;
        private readonly List<(string Name, string Value)> _headers = [];
        private bool _keepAlive;
        private Stream? _body;

        public Response(Stream transport, bool keepAlive)
        {
            _transport = transport;
            _keepAlive = keepAlive;
        }

        public Stream Body => _body ??= new BodyStream(this);

        public long ContentLength { get; set; }

        public string ContentType { get; set; } = string.Empty;

        public int StatusCode { get; set; } = (int)HttpStatusCode.OK;

        public bool HeadersSent { get; private set; }

        public void AddHeader(string name, string value) => _headers.Add((name, value));

        /// <summary>Discards everything set so far and answers with a bare status instead.</summary>
        public void Reset(HttpStatusCode status)
        {
            StatusCode = (int)status;
            ContentType = string.Empty;
            ContentLength = 0;
            _headers.Clear();
            _keepAlive = false;
        }

        /// <summary>Sends the head if the body has not already, and flushes.</summary>
        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            await SendHeadersAsync(cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Writes part of the body, sending the head first if it has not gone yet.</summary>
        public async ValueTask WriteBodyAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await SendHeadersAsync(cancellationToken).ConfigureAwait(false);
            await _transport.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public Task FlushAsync(CancellationToken cancellationToken) => _transport.FlushAsync(cancellationToken);

        public async ValueTask SendHeadersAsync(CancellationToken cancellationToken)
        {
            if (HeadersSent)
            {
                return;
            }

            HeadersSent = true;
            await _transport.WriteAsync(Encoding.ASCII.GetBytes(Head()), cancellationToken).ConfigureAwait(false);
        }

        private string Head()
        {
            var head = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {StatusCode} {ReasonPhrase(StatusCode)}\r\n");

            if (ContentType.Length > 0)
            {
                head.Append(CultureInfo.InvariantCulture, $"Content-Type: {ContentType}\r\n");
            }

            // RFC 9110 8.6: never on a 204, which has no content to measure.
            if (StatusCode != (int)HttpStatusCode.NoContent)
            {
                head.Append(CultureInfo.InvariantCulture, $"Content-Length: {ContentLength}\r\n");
            }

            foreach (var (name, value) in _headers)
            {
                head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            }

            head.Append(_keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
            return head.Append("\r\n").ToString();
        }

        private static string ReasonPhrase(int status) => status switch
        {
            200 => "OK",
            204 => "No Content",
            206 => "Partial Content",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            416 => "Range Not Satisfiable",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => string.Empty,
        };
    }

    /// <summary>The body of a <see cref="Response"/>: sends the head before the first byte.</summary>
    /// <remarks>
    /// Writes through the response rather than holding the connection itself: the connection
    /// belongs to the server, and this is only a view of it for one response.
    /// </remarks>
    private sealed class BodyStream(Response response) : Stream
    {
        private AtomicDisposal _disposal = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _disposal.ThrowIfDisposed(this);
            return response.WriteBodyAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => response.FlushAsync(cancellationToken);

        public override void Flush() => throw new NotSupportedException("Synchronous I/O is not supported. Use FlushAsync.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Synchronous I/O is not supported. Use WriteAsync.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Ends this view of the response. The connection underneath is left open, since it belongs to
        /// the server and may carry the next request.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            _disposal.MarkDisposed();
            base.Dispose(disposing);
        }
    }
}
