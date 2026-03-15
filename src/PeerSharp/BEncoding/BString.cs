using System.Text;

namespace PeerSharp.BEncoding;

internal class BString : IBNode
{
    public BString(ReadOnlyMemory<byte> val) => Value = val;

    public BString(byte[] val) => Value = val;

    public string Text => Encoding.UTF8.GetString(Value.Span);
    public BencodeType Type => BencodeType.String;
    public ReadOnlyMemory<byte> Value { get; set; }

    // Compat
    public override string ToString()
    {
        return Text;
    }
}
