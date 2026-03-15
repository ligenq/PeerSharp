namespace PeerSharp.BEncoding;

internal class BNumber : IBNode
{
    public BNumber(long val) => Value = val;

    public BencodeType Type => BencodeType.Integer;
    public long Value { get; set; }

    public override string ToString()
    {
        return Value.ToString();
    }
}
