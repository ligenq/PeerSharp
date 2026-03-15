using System.Buffers;
using System.Text;

namespace PeerSharp.BEncoding;

internal static class BencodeWriter
{
    public static byte[] Write(IBNode node)
    {
        using var writer = new PooledBufferWriter();
        Write(node, writer);
        return writer.WrittenSpan.ToArray();
    }

    public static void Write(IBNode node, IBufferWriter<byte> writer)
    {
        switch (node.Type)
        {
            case BencodeType.Integer:
                if (node is BNumber n)
                {
                    WriteByte(writer, (byte)'i');
                    WriteAsciiLong(n.Value, writer);
                    WriteByte(writer, (byte)'e');
                }
                break;

            case BencodeType.String:
                if (node is BString s)
                {
                    WriteAsciiInt(s.Value.Length, writer);
                    WriteByte(writer, (byte)':');
                    writer.Write(s.Value.Span);
                }
                break;

            case BencodeType.List:
                if (node is BList l)
                {
                    WriteByte(writer, (byte)'l');
                    foreach (var item in l.List)
                    {
                        Write(item, writer);
                    }

                    WriteByte(writer, (byte)'e');
                }
                break;

            case BencodeType.Dictionary:
                if (node is BDict d)
                {
                    WriteByte(writer, (byte)'d');
                    // Must be sorted by key
                    var sortedKeys = new List<string>(d.Dict.Keys);
                    sortedKeys.Sort(StringComparer.Ordinal);

                    foreach (var key in sortedKeys)
                    {
                        WriteStringKey(key, writer);
                        Write(d.Dict[key], writer);
                    }
                    WriteByte(writer, (byte)'e');
                }
                break;
        }
    }

    public static BencodeResult WriteToResult(IBNode node)
    {
        var writer = new PooledBufferWriter();
        Write(node, writer);
        return new BencodeResult(writer);
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static void WriteAsciiInt(int value, IBufferWriter<byte> writer)
    {
        Span<byte> buffer = writer.GetSpan(11); // Max int32 is 10 digits + sign
        if (value.TryFormat(buffer, out int written))
        {
            writer.Advance(written);
        }
    }

    private static void WriteAsciiLong(long value, IBufferWriter<byte> writer)
    {
        Span<byte> buffer = writer.GetSpan(20); // Max int64 is 19 digits + sign
        if (value.TryFormat(buffer, out int written))
        {
            writer.Advance(written);
        }
    }

    private static void WriteStringKey(string key, IBufferWriter<byte> writer)
    {
        // Use Latin1 encoding for dictionary keys because bencode keys are arbitrary
        // byte strings (not UTF8). Latin1 preserves all byte values 0-255.
        int byteCount = Encoding.Latin1.GetByteCount(key);
        WriteAsciiInt(byteCount, writer);
        WriteByte(writer, (byte)':');

        var span = writer.GetSpan(byteCount);
        Encoding.Latin1.GetBytes(key, span);
        writer.Advance(byteCount);
    }
}

internal readonly struct BencodeResult : IDisposable
{
    private readonly PooledBufferWriter _writer;

    public BencodeResult(PooledBufferWriter writer) => _writer = writer;

    public ReadOnlyMemory<byte> Memory => _writer.WrittenMemory;
    public ReadOnlySpan<byte> Span => _writer.WrittenSpan;

    public void Dispose() => _writer.Dispose();
}
