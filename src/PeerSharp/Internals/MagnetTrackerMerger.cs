namespace PeerSharp.Internals;

internal static class MagnetTrackerMerger
{
    public static void Merge(TorrentFileMetadata metadata, MagnetLink magnetLink)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(magnetLink);

        if (magnetLink.Trackers.Count == 0)
        {
            return;
        }

        // Ensure tier 0 exists, seeded from whatever existing announce data we have.
        if (metadata.AnnounceTiers.Count == 0)
        {
            var seedTier = new List<string>();
            if (metadata.AnnounceList.Count > 0)
            {
                seedTier.AddRange(metadata.AnnounceList);
            }
            else if (!string.IsNullOrWhiteSpace(metadata.Announce))
            {
                seedTier.Add(metadata.Announce);
            }

            metadata.AnnounceTiers.Add(seedTier);
        }

        var tier = metadata.AnnounceTiers[0];
        foreach (var tracker in magnetLink.Trackers)
        {
            if (!tier.Any(t => string.Equals(t, tracker, StringComparison.OrdinalIgnoreCase)))
            {
                tier.Add(tracker);
            }
        }

        metadata.AnnounceList = [.. tier
            .Concat(metadata.AnnounceList)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (string.IsNullOrWhiteSpace(metadata.Announce) && metadata.AnnounceList.Count > 0)
        {
            metadata.Announce = metadata.AnnounceList[0];
        }
    }
}
