using System.Security.Cryptography;

namespace PeerSharp.EndToEnd;

/// <summary>
/// Reads a torrent's exact-topic identity straight out of the file.
/// </summary>
/// <remarks>
/// Deliberately written here rather than taken from PeerSharp. The harness measures PeerSharp, so
/// anything it needs in order to set a measurement up has to come from somewhere else - a magnet
/// built with PeerSharp's own parser would make a broken parser look like a fast one.
/// </remarks>
internal static class TorrentInfoHash
{
    /// <summary>
    /// Returns the complete magnet exact topic. Pure v2 torrents use the SHA-256 multihash form;
    /// v1 and hybrid torrents use their SHA-1 identity.
    /// </summary>
    public static string ReadExactTopic(string torrentPath, bool v2Only)
    {
        byte[] data = File.ReadAllBytes(torrentPath);
        if (data.Length == 0 || data[0] != (byte)'d')
        {
            throw new InvalidOperationException($"'{torrentPath}' is not a bencoded dictionary.");
        }

        // Walk the top-level dictionary's keys until "info", then hash its raw bytes: the info hash
        // is over the encoding as it appears in the file, not over a re-encoding of it.
        int i = 1;
        while (i < data.Length && data[i] != (byte)'e')
        {
            int keyEnd = SkipValue(data, i);
            int colon = Array.IndexOf(data, (byte)':', i);
            string key = System.Text.Encoding.ASCII.GetString(data, colon + 1, keyEnd - colon - 1);
            int valueEnd = SkipValue(data, keyEnd);

            if (key == "info")
            {
                ReadOnlySpan<byte> info = data.AsSpan(keyEnd, valueEnd - keyEnd);
                if (v2Only)
                {
                    // BEP 52 magnet links carry the full SHA-256 info hash as a multihash:
                    // 0x12 = sha2-256, 0x20 = 32-byte digest.
                    return "urn:btmh:1220" + Convert.ToHexStringLower(SHA256.HashData(info));
                }

                return "urn:btih:" + Convert.ToHexStringLower(SHA1.HashData(info));
            }

            i = valueEnd;
        }

        throw new InvalidOperationException($"'{torrentPath}' has no info dictionary.");
    }

    /// <summary>Returns the index just past the bencoded value starting at <paramref name="start"/>.</summary>
    private static int SkipValue(byte[] data, int start)
    {
        byte marker = data[start];
        if (marker == (byte)'d' || marker == (byte)'l')
        {
            int i = start + 1;
            while (data[i] != (byte)'e')
            {
                i = SkipValue(data, i);
            }

            return i + 1;
        }

        if (marker == (byte)'i')
        {
            return Array.IndexOf(data, (byte)'e', start) + 1;
        }

        int colon = Array.IndexOf(data, (byte)':', start);
        int length = int.Parse(System.Text.Encoding.ASCII.GetString(data, start, colon - start));
        return colon + 1 + length;
    }
}
