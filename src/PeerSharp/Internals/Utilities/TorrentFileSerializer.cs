using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Internals.Utilities;

internal static class TorrentFileSerializer
{
    public static byte[]? BuildTorrentBytes(TorrentFileMetadata metadata)
    {
        if (metadata.InfoBytes == null || metadata.InfoBytes.Length == 0)
        {
            return null;
        }

        if (BencodeParser.Parse(metadata.InfoBytes) is not BDict info)
        {
            return null;
        }

        var root = new BDict();

        string? announce = metadata.Announce;
        var tiers = new List<List<string>>();
        if (metadata.AnnounceTiers.Count > 0)
        {
            foreach (var tier in metadata.AnnounceTiers)
            {
                if (tier.Count > 0)
                {
                    tiers.Add([.. tier]);
                }
            }
        }
        else if (metadata.AnnounceList.Count > 0)
        {
            tiers.Add([.. metadata.AnnounceList]);
        }

        if (string.IsNullOrWhiteSpace(announce))
        {
            if (tiers.Count > 0 && tiers[0].Count > 0)
            {
                announce = tiers[0][0];
            }
        }
        else if (tiers.Count == 0)
        {
            tiers.Add([announce]);
        }

        if (!string.IsNullOrWhiteSpace(announce))
        {
            root.Dict["announce"] = new BString(Encoding.UTF8.GetBytes(announce));
        }

        if (tiers.Count > 0)
        {
            var announceList = new BList();
            foreach (var tier in tiers)
            {
                var tierList = new BList();
                foreach (var url in tier)
                {
                    tierList.List.Add(new BString(Encoding.UTF8.GetBytes(url)));
                }
                announceList.List.Add(tierList);
            }
            root.Dict["announce-list"] = announceList;
        }

        if (metadata.WebSeedUrls.Count == 1)
        {
            root.Dict["url-list"] = new BString(Encoding.UTF8.GetBytes(metadata.WebSeedUrls[0]));
        }
        else if (metadata.WebSeedUrls.Count > 1)
        {
            var urlList = new BList();
            foreach (var url in metadata.WebSeedUrls)
            {
                urlList.List.Add(new BString(Encoding.UTF8.GetBytes(url)));
            }
            root.Dict["url-list"] = urlList;
        }

        if (metadata.PieceLayers.Count > 0)
        {
            var pieceLayers = new BDict();
            foreach (var kvp in metadata.PieceLayers)
            {
                var key = Encoding.Latin1.GetString(kvp.Key);
                pieceLayers.Dict[key] = new BString(kvp.Value);
            }
            root.Dict["piece layers"] = pieceLayers;
        }

        root.Dict["info"] = info;

        return BencodeWriter.Write(root);
    }
}
