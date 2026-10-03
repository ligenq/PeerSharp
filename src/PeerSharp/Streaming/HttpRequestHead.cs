using System.Text;
using System.Globalization;

namespace PeerSharp.Streaming;

/// <summary>
/// The request line and headers of one HTTP/1.x request, as far as a stream server needs them.
/// </summary>
/// <remarks>
/// Deliberately small. The server answers GET, HEAD and OPTIONS on one path, so it needs the method,
/// the path, the range, and enough of the connection headers to know whether the connection
/// outlives the request. Anything it cannot read with certainty is rejected rather than guessed at.
/// </remarks>
internal sealed class HttpRequestHead : IHttpStreamRequest
{
    private HttpRequestHead(string method, string path, bool isHttp11, Dictionary<string, string> headers)
    {
        Method = method;
        Path = path;
        IsHttp11 = isHttp11;
        Headers = headers;
    }

    public string Method { get; }

    /// <summary>The request target's path, without its query string.</summary>
    public string Path { get; }

    public bool IsHttp11 { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? RangeHeader => Headers.GetValueOrDefault("Range");

    /// <summary>
    /// Whether the request carries a body. The server accepts none, and without reading one it
    /// cannot find where the next request on the connection begins, so such a connection is closed
    /// after its response.
    /// </summary>
    public bool HasBody =>
        Headers.ContainsKey("Transfer-Encoding")
        || (Headers.TryGetValue("Content-Length", out var length) && long.Parse(length, CultureInfo.InvariantCulture) != 0);

    /// <summary>
    /// Whether the client expects the connection to stay open after the response: by default in
    /// HTTP/1.1, and only when asked for in HTTP/1.0.
    /// </summary>
    public bool WantsKeepAlive
    {
        get
        {
            var tokens = Headers.GetValueOrDefault("Connection", string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Contains("close", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsHttp11 || tokens.Contains("keep-alive", StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Parses a request head: everything before the blank line, without the blank line itself.
    /// </summary>
    /// <returns>The request, or null when it is not a well-formed HTTP/1.0 or 1.1 request.</returns>
    public static HttpRequestHead? TryParse(ReadOnlySpan<byte> head)
    {
        // Request heads are ASCII. A byte outside it is a malformed request rather than text to
        // decode, and decoding it anyway would turn it into a '?' that could then match something.
        if (head.ContainsAnyExceptInRange((byte)0x00, (byte)0x7F))
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || requestLine[1].Length == 0)
        {
            return null;
        }
        if (!requestLine[0].All(IsTokenCharacter) || requestLine[1].Any(c => c <= ' ' || c == 0x7F)) return null;

        bool isHttp11;
        switch (requestLine[2])
        {
            case "HTTP/1.1":
                isHttp11 = true;
                break;
            case "HTTP/1.0":
                isHttp11 = false;
                break;
            default:
                return null;
        }

        var path = PathOf(requestLine[1]);
        if (path == null)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            // A line starting with whitespace continues the previous header. RFC 9112 obsoletes
            // this folding and lets a server reject it, which is safer than joining values wrongly.
            if (line.Length == 0 || line[0] is ' ' or '\t')
            {
                return null;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].All(IsTokenCharacter)
                || line[(colon + 1)..].Any(c => (c < ' ' && c != '\t') || c == 0x7F))
            {
                return null;
            }

            string name = line[..colon];
            string value = line[(colon + 1)..].Trim(' ', '\t');
            if (headers.TryGetValue(name, out string? previous))
            {
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Range", StringComparison.OrdinalIgnoreCase)) return null;
                value = previous + "," + value;
            }
            headers[name] = value;
        }

        if (headers.TryGetValue("Content-Length", out string? length)
            && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || headers.ContainsKey("Transfer-Encoding"))) return null;

        return new HttpRequestHead(requestLine[0], path, isHttp11, headers);
    }

    private static bool IsTokenCharacter(char c) => char.IsAsciiLetterOrDigit(c)
        || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary>The path of a request target, in either origin or absolute form.</summary>
    private static string? PathOf(string target)
    {
        if (target.StartsWith('/'))
        {
            int query = target.IndexOf('?');
            return query < 0 ? target : target[..query];
        }

        // Absolute form is sent to proxies, but servers must accept it too (RFC 9112 3.2.2).
        return Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsolutePath
            : null;
    }
}
