using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Upload slot allocation and rechoking.
/// </summary>
/// <remarks>
/// <para>
/// The slot count is what turns an upload limit into a number of peers to serve, through a chain of
/// clamps against configured minimum and maximum, the limit itself, and how many peers are actually
/// connected. Clamps interacting is precisely where a plausible configuration produces an
/// implausible answer, and the answers here matter in both directions: too few slots and the upload
/// limit goes unused, too many and every peer gets a share too thin to be worth anything.
/// </para>
/// <para>
/// Rechoking is checked for the promises it makes to peers rather than for the exact set it picks -
/// the optimistic unchoke is deliberately random, so the choice is not reproducible and only the
/// bounds around it are.
/// </para>
/// </remarks>
public class PeerChokerPropertyTests
{
    [Fact]
    public void TheSlotCountStaysInsideItsConfiguredBounds()
    {
        Configurations().Sample(configuration =>
        {
            var choker = Build(configuration, out int minSlots, out int maxSlots);

            int slots = choker.GetUploadSlotsForTesting(configuration.ConnectedCount);

            Assert.InRange(slots, minSlots, maxSlots);
            Assert.True(slots >= 1, "a torrent with no upload slots can never unchoke anyone");
        }, iter: 20_000);
    }

    [Fact]
    public void SlotsAreNeverHandedOutToPeersThatAreNotThere()
    {
        // Beyond the configured minimum, which deliberately holds slots open for peers yet to
        // arrive, there is no point in more slots than connections.
        Configurations().Sample(configuration =>
        {
            var choker = Build(configuration, out int minSlots, out _);

            int slots = choker.GetUploadSlotsForTesting(configuration.ConnectedCount);

            Assert.True(
                slots <= Math.Max(minSlots, configuration.ConnectedCount),
                $"{slots} slots for {configuration.ConnectedCount} connections");
        }, iter: 20_000);
    }

    [Fact]
    public void MoreBandwidthNeverMeansFewerSlots()
    {
        // The whole purpose of deriving slots from the limit. A non-monotonic step would mean
        // raising the upload limit could reduce how many peers are served.
        Gen.Select(Configurations(), Gen.Long[0, 50_000_000]).Sample((configuration, extra) =>
        {
            var choker = Build(configuration, out _, out _);
            int before = choker.GetUploadSlotsForTesting(configuration.ConnectedCount);

            var raised = Build(configuration with { UploadLimit = SaturatingAdd(configuration.UploadLimit, extra) }, out _, out _);
            int after = raised.GetUploadSlotsForTesting(configuration.ConnectedCount);

            // A limit of 0 means unlimited, so it is not a point on this curve.
            if (configuration.UploadLimit > 0)
            {
                Assert.True(after >= before, $"raising the limit by {extra} took slots from {before} to {after}");
            }
        }, iter: 10_000);
    }

    [Fact]
    public void MorePeersNeverMeansFewerSlots()
    {
        Gen.Select(Configurations(), Gen.Int[0, 200]).Sample((configuration, extra) =>
        {
            var choker = Build(configuration, out _, out _);

            Assert.True(
                choker.GetUploadSlotsForTesting(configuration.ConnectedCount + extra)
                    >= choker.GetUploadSlotsForTesting(configuration.ConnectedCount),
                "connecting another peer reduced the slot count");
        }, iter: 10_000);
    }

    [Fact]
    public void APeerThatIsNotInterestedIsNeverUnchoked()
    {
        // An upload slot given to a peer that has not asked for data is a slot doing nothing.
        Peers().Sample(speeds =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            torrent.Settings.Connection.UploadSlotsMin = 2;
            torrent.Settings.Connection.UploadSlotsMax = 6;
            var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);

            var peers = speeds.Select(s =>
            {
                var peer = new PolicyTestPeer(torrent);
                peer.SetInterested(s.Interested);
                peer.SetSpeed(s.Speed);
                return peer;
            }).ToList();

            choker.Rechoke(peers, peers.Count);

            foreach (var peer in peers.Where(p => !p.PeerInterested))
            {
                Assert.True(peer.AmChoking, "an uninterested peer was given an upload slot");
            }
        }, iter: 3_000);
    }

    [Fact]
    public void UnchokingStaysBoundedByTheSlotCount()
    {
        // Rechoking may exceed the nominal slot count a little - it keeps peers that are still close
        // to the best speed rather than dropping them for a marginal gain, and adds an optimistic
        // unchoke on top - but it has to stay bounded, because every unchoked peer is a share of the
        // same upload limit.
        Peers().Sample(speeds =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            torrent.Settings.Connection.UploadSlotsMin = 2;
            torrent.Settings.Connection.UploadSlotsMax = 6;
            var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);

            var peers = speeds.Select(s =>
            {
                var peer = new PolicyTestPeer(torrent);
                peer.SetInterested(s.Interested);
                peer.SetSpeed(s.Speed);
                return peer;
            }).ToList();

            choker.Rechoke(peers, peers.Count);

            int slots = choker.GetUploadSlotsForTesting(peers.Count);
            int unchoked = peers.Count(peer => !peer.AmChoking);

            Assert.True(unchoked <= slots + 3, $"{unchoked} peers unchoked against {slots} slots");
        }, iter: 3_000);
    }

    [Fact]
    public void EveryInterestedPeerIsServedWhenThereIsRoomForThemAll()
    {
        // With fewer interested peers than slots there is nothing to ration, so choking any of them
        // would leave upload capacity unused for no reason.
        Gen.Int[0, 3].Array[0, 3].Sample(speeds =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            torrent.Settings.Connection.UploadSlotsMin = 8;
            torrent.Settings.Connection.UploadSlotsMax = 8;
            var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);

            var peers = speeds.Select(speed =>
            {
                var peer = new PolicyTestPeer(torrent);
                peer.SetInterested(true);
                peer.SetSpeed(speed);
                return peer;
            }).ToList();

            choker.Rechoke(peers, peers.Count);

            foreach (var peer in peers)
            {
                Assert.False(peer.AmChoking, "an interested peer was choked while slots were free");
            }
        }, iter: 2_000);
    }

    private static Gen<(bool Interested, int Speed)[]> Peers() =>
        Gen.Select(Gen.Bool, Gen.Int[0, 1_000_000]).Array[0, 12];

    private static Gen<Configuration> Configurations()
    {
        return Gen.Select(
            Gen.Int[-2, 40],
            Gen.Int[-2, 40],
            Gen.Long[0, 50_000_000],
            Gen.Int[-2, 500_000],
            Gen.Int[0, 200])
            .Select(t => new Configuration(t.Item1, t.Item2, t.Item3, t.Item4, t.Item5));
    }

    /// <summary>
    /// Settings as a caller may actually leave them, negatives and inverted bounds included - these
    /// are public properties with no validation on the way in.
    /// </summary>
    private readonly record struct Configuration(
        int MinSlots,
        int MaxSlots,
        long UploadLimit,
        int TargetPerSlot,
        int ConnectedCount);

    private static PeerChoker Build(Configuration configuration, out int minSlots, out int maxSlots)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UploadSlotsMin = configuration.MinSlots;
        torrent.Settings.Connection.UploadSlotsMax = configuration.MaxSlots;
        torrent.Settings.Connection.TargetUploadPerSlotBytesPerSec = configuration.TargetPerSlot;
        torrent.Settings.Transfer.MaxUploadSpeed = configuration.UploadLimit;

        // The same normalisation the choker applies, so the bounds asserted against are the ones it
        // actually works to rather than the raw settings.
        minSlots = Math.Max(1, configuration.MinSlots);
        maxSlots = Math.Max(minSlots, configuration.MaxSlots);

        return new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);
    }

    private static long SaturatingAdd(long value, long addend)
        => value > long.MaxValue - addend ? long.MaxValue : value + addend;
}
