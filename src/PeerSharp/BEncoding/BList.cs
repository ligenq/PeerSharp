namespace PeerSharp.BEncoding;

internal class BList : IBNode
{
    public List<IBNode> List { get; } = new();
    public BencodeType Type => BencodeType.List;

    public override string ToString()
    {
        return $"List[{List.Count}]";
    }
}
