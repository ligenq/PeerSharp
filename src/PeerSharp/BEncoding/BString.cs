using System.Text;

namespace PeerSharp.BEncoding;

internal class BString : IBNode
{
    private ReadOnlyMemory<byte> _value;
    private string? _text;

    public BString(ReadOnlyMemory<byte> val) => Value = val;

    public BString(byte[] val) => Value = val;

    public string Text => _text ??= Encoding.UTF8.GetString(Value.Span);
    public BencodeType Type => BencodeType.String;

    public ReadOnlyMemory<byte> Value
    {
        get => _value;
        set
        {
            _value = value;
            _text = null;
        }
    }

    // Compat
    public override string ToString()
    {
        return Text;
    }
}
