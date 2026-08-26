using PeerSharp.BEncoding;

namespace PeerSharp.Internals.Extensions;

internal class ExtensionHandshake
{
    public string Client { get; set; } = string.Empty;
    public Dictionary<string, int> MessageIds { get; set; } = [];
    public int? MetadataSize { get; set; }
    public byte[]? YourIp { get; set; }

    /// <summary>
    /// BEP 10: <c>p</c>, "local TCP listen port". The port a connection arrives from is ephemeral and
    /// says nothing about where that peer accepts connections, so without this we cannot reconnect to
    /// an inbound peer later, or tell anyone else how to reach them.
    /// </summary>
    public int? ListenPort { get; set; }

    /// <summary>
    /// BEP 10: <c>reqq</c>, "the number of outstanding request messages this client supports without
    /// dropping any". Null when the peer did not say, which is not the same as zero - a peer that stays
    /// silent is telling us nothing, and every client picks its own assumption in that case.
    /// </summary>
    public int? RequestQueueDepth { get; set; }

    /// <summary>
    /// BEP 21: <c>upload_only</c>. "Setting the value of this key to 1 indicates that this peer is not
    /// interested in downloading anything" - a seed, or a partial seed that has everything it selected.
    /// </summary>
    public bool IsUploadOnly { get; set; }

    /// <summary>
    /// Returns the one-byte message id when this handshake enables an extension. BEP 10 reserves
    /// zero for the extension handshake itself and defines an extension value of zero as disabled.
    /// </summary>
    public int? GetEnabledMessageId(string name)
    {
        return MessageIds.TryGetValue(name, out int id) && id is > 0 and <= byte.MaxValue
            ? id
            : null;
    }

    public static ExtensionHandshake Parse(BDict dict)
    {
        var handshake = new ExtensionHandshake();
        if (dict.Get("m") is BDict m)
        {
            foreach (var kvp in m.Dict)
            {
                if (kvp.Value is BNumber { Value: >= 0 and <= byte.MaxValue } n)
                {
                    handshake.MessageIds[kvp.Key] = (int)n.Value;
                }
            }
        }

        handshake.Client = dict.GetString("v") ?? string.Empty;
        handshake.MetadataSize = (int?)dict.GetLong("metadata_size");
        handshake.YourIp = dict.GetBytes("yourip")?.ToArray();

        // BEP 21 specifies the value 1. Anything else non-zero is treated the same rather than
        // ignored - the intent is unambiguous and this is a hint, not a security boundary.
        handshake.IsUploadOnly = (dict.GetLong("upload_only") ?? 0) != 0;

        // A port of zero means "not listening", which is legitimate for a peer behind a NAT it cannot
        // map, and is not the same as a peer that told us nothing.
        if (dict.GetLong("p") is >= 0 and <= 65535 and var port)
        {
            handshake.ListenPort = (int)port;
        }

        // Only a positive depth means anything. Zero or negative is a peer saying it accepts no
        // requests, which no client means and which would stall us if we believed it.
        if (dict.GetLong("reqq") is > 0 and var reqq)
        {
            handshake.RequestQueueDepth = (int)Math.Min(reqq, int.MaxValue);
        }

        return handshake;
    }

    public BDict ToBencode()
    {
        var dict = new BDict();
        var m = new BDict();
        foreach (var kvp in MessageIds)
        {
            m.Dict[kvp.Key] = new BNumber(kvp.Value);
        }
        dict.Dict["m"] = m;

        if (!string.IsNullOrEmpty(Client))
        {
            dict.Dict["v"] = new BString(System.Text.Encoding.UTF8.GetBytes(Client));
        }

        if (MetadataSize.HasValue)
        {
            dict.Dict["metadata_size"] = new BNumber(MetadataSize.Value);
        }

        if (YourIp != null)
        {
            dict.Dict["yourip"] = new BString(YourIp);
        }

        if (ListenPort.HasValue)
        {
            dict.Dict["p"] = new BNumber(ListenPort.Value);
        }

        if (RequestQueueDepth.HasValue)
        {
            dict.Dict["reqq"] = new BNumber(RequestQueueDepth.Value);
        }

        // Only emitted when set: BEP 21 defines the presence of the key with value 1, and a peer that
        // is still downloading has nothing to say here.
        if (IsUploadOnly)
        {
            dict.Dict["upload_only"] = new BNumber(1);
        }

        return dict;
    }
}
