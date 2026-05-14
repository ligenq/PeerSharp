using PeerSharp.BEncoding;

namespace PeerSharp.Internals.Extensions;

internal class ExtensionHandshake
{
    public string Client { get; set; } = string.Empty;
    public Dictionary<string, int> MessageIds { get; set; } = [];
    public int? MetadataSize { get; set; }
    public byte[]? YourIp { get; set; }

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

        return dict;
    }
}
