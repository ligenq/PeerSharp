namespace PeerSharp.Internals.Utilities;

internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        // Normalize
        input = input.Trim().Replace("=", "").ToUpperInvariant();

        int bits = 0;
        int value = 0;
        var bytes = new List<byte>();

        foreach (char c in input)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0)
            {
                throw new FormatException("Invalid Base32 character");
            }

            value = (value << 5) | idx;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((value >> bits) & 0xFF));
                value &= (1 << bits) - 1;
            }
        }

        return bytes.ToArray();
    }
}
