using System.Text;

namespace PeerSharp.BEncoding;

internal static class BencodeParser
{
    // SECURITY: Maximum items in a list or dictionary to prevent memory exhaustion
    private const int MaxCollectionItems = 100000;

    // SECURITY: Maximum recursion depth to prevent stack overflow attacks
    private const int MaxRecursionDepth = 100;

    // SECURITY: Maximum string length (10MB) to prevent memory exhaustion
    private const int MaxStringLength = 10 * 1024 * 1024;

    // SECURITY: Maximum total elements across entire structure to prevent DoS
    private const int MaxTotalElements = 500000;

    public static IBNode? Parse(byte[] data)
    {
        int pos = 0;
        int elementCount = 0;
        return Parse(data, ref pos, 0, ref elementCount);
    }

    // Parse and return consumed byte count
    public static (IBNode? Node, int Consumed) ParseWithConsumed(byte[] data)
    {
        int pos = 0;
        int elementCount = 0;
        var node = Parse(data, ref pos, 0, ref elementCount);
        return (node, pos);
    }

    private static IBNode? Parse(byte[] data, ref int pos, int depth, ref int elementCount)
    {
        // SECURITY: Prevent stack overflow from deeply nested structures
        if (depth > MaxRecursionDepth)
        {
            throw new FormatException($"SECURITY: Bencode recursion depth exceeds maximum ({MaxRecursionDepth}). Possible attack.");
        }

        // SECURITY: Prevent memory exhaustion from huge structures
        elementCount++;
        if (elementCount > MaxTotalElements)
        {
            throw new FormatException($"SECURITY: Bencode total element count exceeds maximum ({MaxTotalElements}). Possible attack.");
        }

        if (pos >= data.Length)
        {
            throw new FormatException("Empty input");
        }

        byte c = data[pos];

        if (c == 'i')
        {
            return ParseInteger(data, ref pos);
        }
        else if (c == 'l')
        {
            return ParseList(data, ref pos, depth, ref elementCount);
        }
        else if (c == 'd')
        {
            return ParseDictionary(data, ref pos, depth, ref elementCount);
        }
        else if (c >= '0' && c <= '9')
        {
            return ParseString(data, ref pos);
        }

        return null;
    }

    private static BDict ParseDictionary(byte[] data, ref int pos, int depth, ref int elementCount)
    {
        pos++; // 'd'
        var dict = new BDict();
        while (pos < data.Length && data[pos] != 'e')
        {
            // SECURITY: Limit items in a single dictionary
            if (dict.Dict.Count >= MaxCollectionItems)
            {
                throw new FormatException($"SECURITY: Bencode dictionary exceeds maximum items ({MaxCollectionItems}). Possible attack.");
            }

            var keyNode = ParseString(data, ref pos);
            // Use Latin1 encoding for dictionary keys because bencode keys are arbitrary
            // byte strings (not UTF8). Latin1 preserves all byte values 0-255.
            var key = Encoding.Latin1.GetString(keyNode.Value.Span);
            var val = Parse(data, ref pos, depth + 1, ref elementCount);
            if (val != null)
            {
                dict.Dict[key] = val;
            }
            else
            {
                break;
            }
        }
        pos++; // 'e'
        return dict;
    }

    private static BNumber ParseInteger(byte[] data, ref int pos)
    {
        pos++; // 'i'
        long val = 0;
        bool negative = false;
        bool hasDigits = false;

        if (pos < data.Length && data[pos] == '-')
        {
            negative = true;
            pos++;
            if (pos >= data.Length)
            {
                throw new FormatException("Unexpected EOF");
            }

            if (data[pos] == '0')
            {
                throw new FormatException("Negative zero is not allowed");
            }
        }

        if (pos < data.Length && data[pos] == '0' && pos + 1 < data.Length && data[pos + 1] != 'e')
        {
            throw new FormatException("Leading zero is not allowed");
        }

        // Accumulate as negative to correctly handle long.MinValue
        // (negative range is larger by 1 than positive range)
        while (pos < data.Length && data[pos] != 'e')
        {
            byte c = data[pos];
            if (c < '0' || c > '9')
            {
                throw new FormatException("Invalid integer");
            }

            hasDigits = true;
            int digit = c - '0';

            try
            {
                checked
                {
                    val = (val * 10) - digit;
                }
            }
            catch (OverflowException)
            {
                throw new FormatException("Integer overflow");
            }

            pos++;
        }

        // Empty integer "ie" is not valid
        if (!hasDigits)
        {
            throw new FormatException("Empty integer");
        }

        if (pos >= data.Length)
        {
            throw new FormatException("Unexpected EOF");
        }

        pos++; // 'e'

        // val is accumulated as negative, negate for positive numbers
        if (!negative)
        {
            try
            {
                checked
                {
                    val = -val;
                }
            }
            catch (OverflowException)
            {
                throw new FormatException("Integer overflow");
            }
        }

        return new BNumber(val);
    }

    private static BList ParseList(byte[] data, ref int pos, int depth, ref int elementCount)
    {
        pos++; // 'l'
        var list = new BList();
        while (pos < data.Length && data[pos] != 'e')
        {
            // SECURITY: Limit items in a single list
            if (list.List.Count >= MaxCollectionItems)
            {
                throw new FormatException($"SECURITY: Bencode list exceeds maximum items ({MaxCollectionItems}). Possible attack.");
            }

            var item = Parse(data, ref pos, depth + 1, ref elementCount);
            if (item != null)
            {
                list.List.Add(item);
            }
            else
            {
                break;
            }
        }
        pos++; // 'e'
        return list;
    }

    private static BString ParseString(byte[] data, ref int pos)
    {
        long len = 0;
        int start = pos;

        if (pos < data.Length && data[pos] == '0' && pos + 1 < data.Length && data[pos + 1] != ':')
        {
            throw new FormatException("Leading zero in string length");
        }

        while (pos < data.Length && data[pos] != ':')
        {
            byte c = data[pos];
            if (c < '0' || c > '9')
            {
                throw new FormatException("Invalid string length");
            }

            try
            {
                checked
                {
                    len = (len * 10) + (c - '0');
                }
            }
            catch (OverflowException)
            {
                throw new FormatException("String length overflow");
            }

            // SECURITY: Limit string length to prevent memory exhaustion
            if (len > MaxStringLength)
            {
                throw new FormatException($"SECURITY: Bencode string length {len} exceeds maximum ({MaxStringLength}). Possible attack.");
            }

            pos++;
        }

        if (pos == start)
        {
            throw new FormatException("Invalid string length");
        }

        pos++; // ':'

        if (len < 0 || pos + len > data.Length)
        {
            throw new FormatException("String out of bounds");
        }

        var slice = new ReadOnlyMemory<byte>(data, pos, (int)len);
        pos += (int)len;

        return new BString(slice);
    }
}
