namespace PeerSharp.Internals.Trackers;

/// <summary>
/// BEP 41: encodes the path and query of a UDP tracker URL as option bytes appended to an announce
/// request.
///
/// <para>
/// A BEP 15 announce packet carries no URL, only an endpoint - so <c>udp://host:2710/announce?passkey=x</c>
/// and <c>udp://host:2710/</c> arrive identically. That is invisible until it matters: a tracker that
/// authenticates on a passkey in the path or query has no way to identify the announce, and the client
/// sees a working socket and no peers. These options are how the missing part of the URL gets there.
/// </para>
/// </summary>
internal static class UdpTrackerUrlData
{
    /// <summary>BEP 41: option-type 0x2, always followed by a length byte.</summary>
    private const byte OptionUrlData = 0x2;

    /// <summary>
    /// One option's data is addressed by a single length byte, so a longer URL has to be split. BEP 41
    /// covers this: "If this option appears more than once, the data fields are concatenated."
    /// </summary>
    private const int MaxDataPerOption = 255;

    /// <summary>
    /// Builds the option bytes for a tracker URL, or an empty span when there is nothing to convey.
    /// </summary>
    /// <param name="url">The tracker announce URL.</param>
    /// <returns>
    /// Option bytes to append at offset 98 of the announce request. Empty when the URL carries no path
    /// or query worth sending, so the packet stays exactly the 98 bytes BEP 15 describes.
    /// </returns>
    public static byte[] Encode(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return [];
        }

        // PathAndQuery keeps the original percent-encoding, which matters: a passkey is opaque and
        // re-encoding or normalising it would change what the tracker is asked to match.
        var pathAndQuery = uri.PathAndQuery;

        // "/" alone tells a tracker nothing it does not already know, and sending nothing keeps the
        // packet byte-identical to what we sent before this extension existed.
        if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery == "/")
        {
            return [];
        }

        // ASCII, not UTF-8: a URI's path and query are already percent-encoded by this point, so every
        // character is in the ASCII range and encoding it as anything wider would be wrong.
        var data = System.Text.Encoding.ASCII.GetBytes(pathAndQuery);

        int optionCount = (data.Length + MaxDataPerOption - 1) / MaxDataPerOption;
        var options = new byte[data.Length + (optionCount * 2)];

        int read = 0, write = 0;
        while (read < data.Length)
        {
            int chunk = Math.Min(MaxDataPerOption, data.Length - read);

            options[write++] = OptionUrlData;
            options[write++] = (byte)chunk;
            data.AsSpan(read, chunk).CopyTo(options.AsSpan(write));

            read += chunk;
            write += chunk;
        }

        // No trailing EndOfOptions: BEP 41 ends parsing at "the end of the packet ... or an
        // EndOfOptions option ... whichever happens first", so the packet boundary already terminates
        // the list and a terminator would only add a byte.
        return options;
    }
}
