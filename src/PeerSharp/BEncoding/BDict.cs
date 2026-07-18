namespace PeerSharp.BEncoding;

internal class BDict : IBNode
{
    public Dictionary<string, IBNode> Dict { get; } = [];
    public BencodeType Type => BencodeType.Dictionary;

    public IBNode? Get(string key)
    {
        return Dict.TryGetValue(key, out IBNode? value) ? value : null;
    }

    public ReadOnlyMemory<byte>? GetBytes(string key)
    {
        // The explicit nullable cast matters: a bare `null` here would convert via
        // byte[] -> ReadOnlyMemory<byte> into an empty (non-null) value instead.
        return Get(key) is BString s ? s.Value : (ReadOnlyMemory<byte>?)null;
    }

    public long? GetLong(string key)
    {
        return Get(key) is BNumber n ? n.Value : null;
    }

    public string? GetString(string key)
    {
        return Get(key) is BString s ? s.Text : null;
    }

    public override string ToString()
    {
        return $"Dict[{Dict.Count}]";
    }
}
