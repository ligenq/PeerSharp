using PeerSharp.BEncoding;

namespace PeerSharp.Internals.Extensions;

internal class ExtensionHandshake
{
    public string Client { get; set; } = string.Empty;
    public Dictionary<string, int> MessageIds { get; set; } = [];
    public int? MetadataSize { get; set; }
    public byte[]? YourIp { get; set; }

    /// <summary>
    /// BEP 21: <c>upload_only</c>. "Setting the value of this key to 1 indicates that this peer is not
    /// interested in downloading anything" - a seed, or a partial seed that has everything it selected.
    /// </summary>
    public bool IsUploadOnly { get; set; }

    public static ExtensionHandshake Parse(BDict dict)
    {
        var handshake = new ExtensionHandshake();
        if (dict.Get("m") is BDict m)
        {
            foreach (var kvp in m.Dict)
            {
                if (kvp.Value is BNumber n)
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

        // Only emitted when set: BEP 21 defines the presence of the key with value 1, and a peer that
        // is still downloading has nothing to say here.
        if (IsUploadOnly)
        {
            dict.Dict["upload_only"] = new BNumber(1);
        }

        return dict;
    }
}
