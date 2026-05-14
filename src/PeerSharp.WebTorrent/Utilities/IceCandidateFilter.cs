using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.WebTorrent.Utilities;

internal static class IceCandidateFilter
{
    public static string FilterUnsupportedIceCandidates(string sdp)
    {
        var builder = new StringBuilder(sdp.Length);
        foreach (var rawLine in sdp.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
                !IsSupportedIceCandidate(line["a=".Length..]))
            {
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        return builder.ToString();
    }

    public static bool IsSupportedIceCandidate(string candidateLine)
    {
        if (candidateLine.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
        {
            candidateLine = candidateLine["a=".Length..];
        }

        if (candidateLine.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            candidateLine = candidateLine["candidate:".Length..];
        }

        var parts = candidateLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6 || !parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(parts[4], out var address) ||
               address.AddressFamily == AddressFamily.InterNetwork;
    }
}
