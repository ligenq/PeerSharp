using System.Runtime.CompilerServices;

namespace PeerSharp.Internals.Utilities;

internal static class UrlEncoding
{
    // BEP 3: The info_hash and peer_id must be percent-encoded.
    // Characters that are NOT to be encoded: [a-zA-Z0-9.\-_~]
    private static readonly bool[] UnreservedChars = CreateUnreservedTable();

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        // Worst case: every byte is encoded as %XX (3 chars)
        int maxLen = data.Length * 3;
        var dest = System.Buffers.ArrayPool<char>.Shared.Rent(maxLen);

        try
        {
            int pos = 0;
            foreach (byte b in data)
            {
                if (IsUnreserved(b))
                {
                    dest[pos++] = (char)b;
                }
                else
                {
                    dest[pos++] = '%';
                    dest[pos++] = "0123456789ABCDEF"[b >> 4];
                    dest[pos++] = "0123456789ABCDEF"[b & 0x0F];
                }
            }
            return new string(dest[..pos]);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(dest);
        }
    }

    public static string Encode(byte[] data)
    {
        return Encode(data.AsSpan());
    }

    private static bool[] CreateUnreservedTable()
    {
        var table = new bool[256];
        for (int i = 'a'; i <= 'z'; i++)
        {
            table[i] = true;
        }

        for (int i = 'A'; i <= 'Z'; i++)
        {
            table[i] = true;
        }

        for (int i = '0'; i <= '9'; i++)
        {
            table[i] = true;
        }

        table['.'] = true;
        table['-'] = true;
        table['_'] = true;
        table['~'] = true;
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnreserved(byte b)
    {
        return UnreservedChars[b];
    }
}
