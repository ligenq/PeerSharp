using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PeerSharp.WebTorrent.Utilities;

internal readonly record struct LocalSubnet(IPAddress Address, IPAddress Mask);

internal static class RemoteSdpReachability
{
    public static IReadOnlyList<LocalSubnet> EnumerateLocalSubnets()
    {
        var subnets = new List<LocalSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(info.Address))
                    {
                        continue;
                    }

                    if (info.IPv4Mask is null || info.IPv4Mask.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    subnets.Add(new LocalSubnet(info.Address, info.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Best-effort: if interfaces can't be enumerated, treat all peers as reachable.
        }

        return subnets;
    }

    public static bool IsLikelyReachable(string sdp, IReadOnlyList<LocalSubnet> localSubnets)
    {
        bool endOfCandidates = false;
        bool sawAnyHostCandidate = false;
        bool sawSupportedHost = false;
        var hostAddresses = new List<IPAddress>();

        foreach (var rawLine in sdp.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Equals("a=end-of-candidates", StringComparison.OrdinalIgnoreCase))
            {
                endOfCandidates = true;
                continue;
            }

            if (!line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Substring("a=candidate:".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int typIndex = Array.IndexOf(parts, "typ");
            if (typIndex < 0 || typIndex + 1 >= parts.Length || parts.Length < 5)
            {
                continue;
            }

            string typ = parts[typIndex + 1];
            if (typ.Equals("srflx", StringComparison.OrdinalIgnoreCase) ||
                typ.Equals("relay", StringComparison.OrdinalIgnoreCase) ||
                typ.Equals("prflx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!typ.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sawAnyHostCandidate = true;

            // ICE candidate format: foundation component transport priority address port typ ...
            // (RFC 5245 §15.1) — connection-address is parts[4].
            string connectionAddress = parts[4];
            if (IPAddress.TryParse(connectionAddress, out var addr))
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    sawSupportedHost = true;
                    hostAddresses.Add(addr);
                }
                // IPv6 literals are stripped by FilterUnsupportedIceCandidates before
                // being forwarded to ICE — treat them as unsupported.
            }
            else
            {
                // Hostname/mDNS: resolvable at pair-check time by the ICE agent.
                sawSupportedHost = true;
            }
        }

        // Trickle ICE: if the peer hasn't sent end-of-candidates yet, more may arrive
        // out-of-band — defer the verdict and let the ICE agent try.
        if (!endOfCandidates)
        {
            return true;
        }

        if (sawSupportedHost && hostAddresses.Count == 0)
        {
            // We saw a supported hostname/mDNS candidate but no IP addresses.
            // These are considered reachable.
            return true;
        }

        foreach (var addr in hostAddresses)
        {
            if (!IsPrivateIPv4(addr))
            {
                return true;
            }

            foreach (var subnet in localSubnets)
            {
                if (IsAddressInSubnet(addr, subnet))
                {
                    return true;
                }
            }
        }

        return !sawAnyHostCandidate;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] switch
        {
            10 => true,
            100 => (bytes[1] & 0xC0) == 64, // 100.64.0.0/10 (CGNAT)
            169 => bytes[1] == 254,         // 169.254.0.0/16 (Link-Local)
            172 => bytes[1] is >= 16 and <= 31,
            192 => bytes[1] == 168,
            _ => false
        };
    }

    private static bool IsAddressInSubnet(IPAddress address, LocalSubnet subnet)
    {
        var addressBytes = address.GetAddressBytes();
        var subnetBytes = subnet.Address.GetAddressBytes();
        var maskBytes = subnet.Mask.GetAddressBytes();

        for (int i = 0; i < addressBytes.Length; i++)
        {
            if ((addressBytes[i] & maskBytes[i]) != (subnetBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }
}
