using System.Text;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// A bencode reader and writer belonging to the synthetic peer alone.
///
/// <para>
/// Deliberately not PeerSharp's. Everything the synthetic peer does is meant to be an independent
/// account of what the BEPs require, and a test that encodes its expectations with the encoder under
/// test cannot fail when that encoder is wrong - it agrees with itself and reports success. That is
/// the exact shape of the defects this suite exists to catch, so the parser used to judge them has to
/// come from somewhere else.
/// </para>
///
/// <para>
/// It handles only what the extension protocol needs: dictionaries, lists, integers and byte strings.
/// It is strict on purpose - a malformed value throws rather than being tolerated - because the
/// synthetic peer is reading bytes PeerSharp produced, and quietly accepting something malformed
/// would turn a wire defect into a passing test.
/// </para>
/// </summary>
internal static class SyntheticBencode
{
    /// <summary>Encodes a value. Dictionary keys are written in the lexicographic order bencode requires.</summary>
    public static byte[] Encode(object value)
    {
        var output = new MemoryStream();
        Write(output, value);
        return output.ToArray();
    }

    private static void Write(Stream output, object value)
    {
        switch (value)
        {
            case IDictionary<string, object> dictionary:
                output.WriteByte((byte)'d');

                // Bencoded dictionaries are sorted by their raw key bytes, and ordinal is what that
                // means for the ASCII keys the extension protocol uses.
                foreach (var pair in dictionary.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
                {
                    Write(output, Encoding.UTF8.GetBytes(pair.Key));
                    Write(output, pair.Value);
                }

                output.WriteByte((byte)'e');
                return;

            case IReadOnlyList<object> list:
                output.WriteByte((byte)'l');
                foreach (object item in list)
                {
                    Write(output, item);
                }

                output.WriteByte((byte)'e');
                return;

            case byte[] bytes:
                WriteAscii(output, bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                output.WriteByte((byte)':');
                output.Write(bytes);
                return;

            case string text:
                Write(output, Encoding.UTF8.GetBytes(text));
                return;

            case long number:
                output.WriteByte((byte)'i');
                WriteAscii(output, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                output.WriteByte((byte)'e');
                return;

            case int number:
                Write(output, (long)number);
                return;

            default:
                throw new ArgumentException($"The synthetic peer cannot bencode a {value?.GetType().Name ?? "null"}.", nameof(value));
        }
    }

    private static void WriteAscii(Stream output, string text)
    {
        foreach (char character in text)
        {
            output.WriteByte((byte)character);
        }
    }

    /// <summary>
    /// Decodes one value. Returns <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>,
    /// <see cref="long"/> or <see cref="byte"/>[].
    /// </summary>
    public static object Decode(ReadOnlySpan<byte> data)
    {
        int position = 0;
        object value = Read(data, ref position);
        return value;
    }

    /// <summary>Decodes a value expected to be a dictionary, with a message naming what was wrong.</summary>
    public static Dictionary<string, object> DecodeDictionary(ReadOnlySpan<byte> data, string what)
    {
        object value;
        try
        {
            value = Decode(data);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"{what} is not valid bencode: {ex.Message}", ex);
        }

        return value as Dictionary<string, object>
            ?? throw new InvalidOperationException($"{what} decoded to {value.GetType().Name}, not a dictionary.");
    }

    private static object Read(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length)
        {
            throw new FormatException("The value ended early.");
        }

        byte marker = data[position];

        if (marker == (byte)'d')
        {
            position++;
            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            while (data[position] != (byte)'e')
            {
                if (Read(data, ref position) is not byte[] key)
                {
                    throw new FormatException("A dictionary key was not a byte string.");
                }

                dictionary[Encoding.UTF8.GetString(key)] = Read(data, ref position);
            }

            position++;
            return dictionary;
        }

        if (marker == (byte)'l')
        {
            position++;
            var list = new List<object>();
            while (data[position] != (byte)'e')
            {
                list.Add(Read(data, ref position));
            }

            position++;
            return list;
        }

        if (marker == (byte)'i')
        {
            position++;
            int end = data[position..].IndexOf((byte)'e');
            if (end < 0)
            {
                throw new FormatException("An integer had no terminator.");
            }

            long number = long.Parse(
                Encoding.ASCII.GetString(data.Slice(position, end)),
                System.Globalization.CultureInfo.InvariantCulture);
            position += end + 1;
            return number;
        }

        int colon = data[position..].IndexOf((byte)':');
        if (colon < 0)
        {
            throw new FormatException($"A byte string had no length terminator (started with 0x{marker:x2}).");
        }

        int length = int.Parse(
            Encoding.ASCII.GetString(data.Slice(position, colon)),
            System.Globalization.CultureInfo.InvariantCulture);
        position += colon + 1;

        if (position + length > data.Length)
        {
            throw new FormatException($"A byte string claimed {length} bytes but only {data.Length - position} remained.");
        }

        byte[] value = data.Slice(position, length).ToArray();
        position += length;
        return value;
    }

    /// <summary>Reads an integer entry, or null when the key is absent.</summary>
    public static long? TryGetInteger(IReadOnlyDictionary<string, object> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out object? value) && value is long number ? number : null;
    }

    /// <summary>Reads a nested dictionary entry, or null when the key is absent.</summary>
    public static Dictionary<string, object>? TryGetDictionary(IReadOnlyDictionary<string, object> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out object? value) ? value as Dictionary<string, object> : null;
    }
}
