namespace PeerSharp.BEncoding;

internal class BDict : IBNode
{
    public Dictionary<string, IBNode> Dict { get; } = new();
    public BencodeType Type => BencodeType.Dictionary;

    public IBNode? Get(string key)
    {
        return Dict.TryGetValue(key, out IBNode? value) ? value : null;
    }

    public ReadOnlyMemory<byte>? GetBytes(string key)
    {
        return Get(key) is BString s ? s.Value : null;
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
