using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class MagnetTrackerMergerTests
{
    [Fact]
    public void Merge_NoMagnetTrackers_DoesNothing()
    {
        var metadata = new TorrentFileMetadata
        {
            Announce = "udp://metadata/announce",
            AnnounceList = ["udp://metadata/announce"],
            AnnounceTiers = [new() { "udp://metadata/announce" }]
        };
        var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:0123456789012345678901234567890123456789");

        MagnetTrackerMerger.Merge(metadata, magnet);

        Assert.Equal("udp://metadata/announce", metadata.Announce);
        Assert.Single(metadata.AnnounceList);
        Assert.Single(metadata.AnnounceTiers);
        Assert.Single(metadata.AnnounceTiers[0]);
    }

    [Fact]
    public void Merge_WithExistingAnnounceList_SeedsFirstTierAndDeduplicatesCaseInsensitive()
    {
        var metadata = new TorrentFileMetadata
        {
            AnnounceList = ["udp://tracker-a/announce"]
        };
        var magnet = MagnetLink.Parse(
            "magnet:?xt=urn:btih:0123456789012345678901234567890123456789" +
            "&tr=udp%3A%2F%2FTRACKER-A%2Fannounce" +
            "&tr=udp%3A%2F%2Ftracker-b%2Fannounce");

        MagnetTrackerMerger.Merge(metadata, magnet);

        Assert.Single(metadata.AnnounceTiers);
        Assert.Equal(new[] { "udp://tracker-a/announce", "udp://tracker-b/announce" }, metadata.AnnounceTiers[0]);
        Assert.Equal(new[] { "udp://tracker-a/announce", "udp://tracker-b/announce" }, metadata.AnnounceList);
        Assert.Equal("udp://tracker-a/announce", metadata.Announce);
    }

    [Fact]
    public void Merge_WithExistingAnnounce_SeedsTierAndPreservesAnnounce()
    {
        var metadata = new TorrentFileMetadata
        {
            Announce = "udp://metadata-primary/announce"
        };
        var magnet = MagnetLink.Parse(
            "magnet:?xt=urn:btih:0123456789012345678901234567890123456789" +
            "&tr=udp%3A%2F%2Fmagnet%2Fannounce");

        MagnetTrackerMerger.Merge(metadata, magnet);

        Assert.Equal(new[] { "udp://metadata-primary/announce", "udp://magnet/announce" }, metadata.AnnounceTiers[0]);
        Assert.Equal(new[] { "udp://metadata-primary/announce", "udp://magnet/announce" }, metadata.AnnounceList);
        Assert.Equal("udp://metadata-primary/announce", metadata.Announce);
    }

    [Fact]
    public void Merge_WithoutExistingTrackers_CreatesTierListAndAnnounce()
    {
        var metadata = new TorrentFileMetadata();
        var magnet = MagnetLink.Parse(
            "magnet:?xt=urn:btih:0123456789012345678901234567890123456789" +
            "&tr=udp%3A%2F%2Fmagnet-a%2Fannounce" +
            "&tr=udp%3A%2F%2Fmagnet-b%2Fannounce");

        MagnetTrackerMerger.Merge(metadata, magnet);

        Assert.Single(metadata.AnnounceTiers);
        Assert.Equal(new[] { "udp://magnet-a/announce", "udp://magnet-b/announce" }, metadata.AnnounceTiers[0]);
        Assert.Equal(metadata.AnnounceTiers[0], metadata.AnnounceList);
        Assert.Equal("udp://magnet-a/announce", metadata.Announce);
    }

    [Fact]
    public void Merge_WhenAllExistingAnnounceFieldsEmpty_StillCreatesTierFromMagnet()
    {
        // Reaches the empty-tier path: no AnnounceTiers, no AnnounceList, no Announce.
        var metadata = new TorrentFileMetadata();
        var magnet = MagnetLink.Parse(
            "magnet:?xt=urn:btih:0123456789012345678901234567890123456789" +
            "&tr=udp%3A%2F%2Fonly%2Fannounce");

        MagnetTrackerMerger.Merge(metadata, magnet);

        var tier = Assert.Single(metadata.AnnounceTiers);
        Assert.Same(tier, metadata.AnnounceTiers[0]);
        Assert.Equal(new[] { "udp://only/announce" }, tier);
        Assert.Equal("udp://only/announce", metadata.Announce);
    }

    [Fact]
    public void Merge_NullMetadata_Throws()
    {
        var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:0123456789012345678901234567890123456789");
        Assert.Throws<ArgumentNullException>(() => MagnetTrackerMerger.Merge(null!, magnet));
    }

    [Fact]
    public void Merge_NullMagnet_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MagnetTrackerMerger.Merge(new TorrentFileMetadata(), null!));
    }
}
