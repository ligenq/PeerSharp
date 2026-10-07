namespace PeerSharp.Streaming;

/// <summary>
/// The media types this library knows how to stream, and the MIME type each is served as.
/// </summary>
/// <remarks>
/// One table for both questions. They were two lists, and had drifted: <c>.m4v</c>, <c>.ts</c>,
/// <c>.m4a</c>, <c>.aac</c> and <c>.opus</c> were offered for streaming and then served as
/// <c>application/octet-stream</c>, which a Chromecast refuses to play.
/// </remarks>
internal static class StreamMediaTypes
{
    /// <summary>The type served for anything not in the table.</summary>
    public const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, (string MimeType, bool IsStreamable)> Types =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = ("video/mp4", true),
            // Apple's own type is video/x-m4v, but the container is MP4 and cast receivers only
            // recognise the standard name.
            [".m4v"] = ("video/mp4", true),
            [".mkv"] = ("video/x-matroska", true),
            [".webm"] = ("video/webm", true),
            [".avi"] = ("video/x-msvideo", true),
            [".mov"] = ("video/quicktime", true),
            [".wmv"] = ("video/x-ms-wmv", true),
            [".flv"] = ("video/x-flv", true),
            [".ts"] = ("video/mp2t", true),
            [".m2ts"] = ("video/mp2t", true),
            [".mpg"] = ("video/mpeg", true),
            [".mpeg"] = ("video/mpeg", true),
            [".mp3"] = ("audio/mpeg", true),
            [".flac"] = ("audio/flac", true),
            [".ogg"] = ("audio/ogg", true),
            // An .opus file is Opus in an Ogg container (RFC 7845), which is registered as audio/ogg.
            [".opus"] = ("audio/ogg", true),
            [".wav"] = ("audio/wav", true),
            [".aac"] = ("audio/aac", true),
            [".m4a"] = ("audio/mp4", true),
            // Subtitles are served alongside the video they belong to, never streamed on their own.
            [".vtt"] = ("text/vtt", false),
            [".srt"] = ("application/x-subrip", false),
        };

    /// <summary>The MIME type to serve a file as, judged by its extension.</summary>
    public static string GetMimeType(string path) =>
        Types.TryGetValue(Path.GetExtension(path), out var type) ? type.MimeType : Fallback;

    /// <summary>Whether a file is audio or video that can be played while it downloads.</summary>
    public static bool IsStreamable(string path) =>
        Types.TryGetValue(Path.GetExtension(path), out var type) && type.IsStreamable;
}
