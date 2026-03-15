using System.Text;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Utility for identifying BitTorrent clients from their Peer ID (BEP 20).
/// </summary>
internal static class ClientIdentification
{
    /// <summary>
    /// Gets a human-readable client name and version from a 20-byte Peer ID.
    /// </summary>
    public static string GetClientName(byte[] peerId)
    {
        if (peerId == null || peerId.Length < 20)
        {
            return "Unknown";
        }

        // BEP 20: Azureus-style Peer ID
        // Format: -AA1111- (dash, two letters for client, 4 version digits, dash)
        if (peerId[0] == '-' && peerId[7] == '-')
        {
            string clientCode = Encoding.ASCII.GetString(peerId, 1, 2);
            string version = Encoding.ASCII.GetString(peerId, 3, 4);
            string clientName = GetAzureusClientName(clientCode);
            return $"{clientName} {FormatVersion(version)}";
        }

        // Shadow's style
        // Format: A111--- (letter, 3 version digits, dashes)
        if (char.IsLetter((char)peerId[0]) && IsDigit(peerId[1]) && IsDigit(peerId[2]) && IsDigit(peerId[3]))
        {
            string clientName = GetShadowClientName((char)peerId[0]);
            string version = Encoding.ASCII.GetString(peerId, 1, 3);
            return $"{clientName} {version[0]}.{version[1]}.{version[2]}";
        }

        return "Unknown Client";
    }

    private static string FormatVersion(string version)
    {
        if (version.Length < 4)
        {
            return version;
        }

        return $"{version[0]}.{version[1]}.{version[2]}.{version[3]}";
    }

    private static string GetAzureusClientName(string code)
    {
        return code switch
        {
            "AG" => "Ares",
            "AR" => "Arctic",
            "AT" => "Artemis",
            "AV" => "Avicora",
            "AZ" => "Vuze",
            "BB" => "BitBuddy",
            "BC" => "BitComet",
            "BF" => "Bitflu",
            "BG" => "BTG",
            "BR" => "BitRocket",
            "BS" => "BTSlave",
            "BT" => "Mainline",
            "BW" => "BitWombat",
            "BX" => "BittorrentX",
            "CD" => "Enhanced CTorrent",
            "CT" => "CTorrent",
            "DE" => "DelugeBT",
            "DP" => "Propagate Data Client",
            "EB" => "EBT",
            "ES" => "electric sheep",
            "FT" => "FoxTorrent",
            "FW" => "FrostWire",
            "FX" => "Freebox BitTorrent",
            "GS" => "GSTorrent",
            "HL" => "Halite",
            "HM" => "Hamachi",
            "HN" => "Hydranode",
            "KG" => "KGet",
            "KT" => "KTorrent",
            "LC" => "LeechCraft",
            "LH" => "LH-ABC",
            "LP" => "LPD",
            "LT" => "libtorrent",
            "lt" => "libTorrent",
            "LW" => "LimeWire",
            "MO" => "MonoTorrent",
            "MP" => "MooPolice",
            "MR" => "Miro",
            "MT" => "MtTorrent",
            "MY" => "MyTorrent",
            "NS" => "Netlyzer BT",
            "NT" => "Nullsoft NTTorrent",
            "OT" => "OmegaTorrent",
            "PD" => "Pando",
            "PS" => "PeerSharp",
            "PT" => "PHPBT",
            "qB" => "qBittorrent",
            "QD" => "QuantumTorrent",
            "QT" => "Qt4 Torrent",
            "RT" => "Retriever",
            "RZ" => "RezTorrent",
            "S~" => "Shareaza",
            "SB" => "Swiftbit",
            "SS" => "SwarmScope",
            "ST" => "SymTorrent",
            "tn" => "Torrent.dot.net",
            "TR" => "Transmission",
            "TS" => "Torrentstorm",
            "TT" => "TuoTu",
            "UL" => "uLeecher!",
            "UM" => "uTorrent for Mac",
            "UT" => "uTorrent",
            "VG" => "Vagaa",
            "WT" => "BitLet",
            "WY" => "FireTorrent",
            "XL" => "Xunlei",
            "XT" => "Xanadu",
            "XX" => "Xtorrent",
            "ZT" => "ZipTorrent",
            _ => $"Unknown ({code})"
        };
    }

    private static string GetShadowClientName(char code)
    {
        return code switch
        {
            'A' => "ABC",
            'O' => "Osprey",
            'Q' => "BTQueue",
            'R' => "Tribler",
            'S' => "Shadow",
            'T' => "BitTornado",
            'U' => "UPnP BitTorrent",
            _ => $"Unknown ({code})"
        };
    }

    private static bool IsDigit(byte b)
    {
        return b >= '0' && b <= '9';
    }
}
