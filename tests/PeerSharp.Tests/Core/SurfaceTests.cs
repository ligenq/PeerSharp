using Microsoft.Extensions.Time.Testing;
using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.PiecePicking;
using System.Net;

namespace PeerSharp.Tests.Core;

/// <summary>
/// The engine and torrent surface a consumer reads and writes, none of which was reached by a test.
/// </summary>
public class ClientEngineSurfaceTests
{
    [Fact]
    public async Task ThePortsAndOptionalSubsystemsReadAsAbsentBeforeTheNetworkStarts()
    {
        // An engine that has not started its network is a state consumers do reach - between Create
        // and StartAsync - so every one of these has to answer rather than dereference a null.
        await using var engine = ClientEngineFactory.Create();

        Assert.Equal(0, engine.BoundTcpPort);
        Assert.Equal(0, engine.BoundUdpPort);
        Assert.NotNull(engine.Bandwidth);
    }

    [Fact]
    public async Task BlocklistEnabledReadsFalseAndIsWritableBeforeTheNetworkExists()
    {
        // The setter is a no-op until there is a blocklist to enable. Throwing here would make the
        // natural "configure then start" order the wrong one.
        await using var engine = ClientEngineFactory.Create();

        Assert.False(engine.BlocklistEnabled);

        engine.BlocklistEnabled = true;
        Assert.False(engine.BlocklistEnabled);
    }

    [Fact]
    public async Task GeoIpEnabledRoundTripsBecauseItsServiceExistsFromTheStart()
    {
        // Unlike the blocklist, GeoIP is not part of the network manager, so this one does hold.
        await using var engine = ClientEngineFactory.Create();

        bool original = engine.GeoIpEnabled;

        engine.GeoIpEnabled = !original;
        Assert.Equal(!original, engine.GeoIpEnabled);

        engine.GeoIpEnabled = original;
        Assert.Equal(original, engine.GeoIpEnabled);
    }

    [Fact]
    public async Task DiscoverInfoHashesSaysWhatIsMissingRatherThanReturningNothing()
    {
        // BEP 51 crawling is the DHT's own sampling call, so without a started DHT there is no
        // crawl to run. An empty stream would read as "the network is quiet" and hide the
        // configuration mistake behind it.
        await using var engine = ClientEngineFactory.Create();

        // Obtained here rather than inside the assertion's lambda: an async iterator does nothing
        // until it is enumerated, so this shows the refusal comes from enumerating it.
        var crawl = engine.DiscoverInfoHashesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in crawl)
            {
                break;
            }
        });

        Assert.Contains("DHT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchingMagnetMetadataWithProgressIsCancellable()
    {
        // The magnet is well-formed but nobody is serving it, so this is the ordinary "no peers ever
        // answered" path a caller has to be able to abandon. The fetch builds a real torrent behind
        // the scenes, so it needs somewhere to put one.
        using var directory = new ScratchDirectory();
        var settings = new Settings();
        settings.Files.DefaultDownloadPath = directory.Path;
        await using var engine = ClientEngineFactory.Create(new TorrentClientOptions { Settings = settings });
        var link = MagnetLink.Parse("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");
        using var cts = new CancellationTokenSource();

        var progress = new Progress<MetadataProgress>();
        var fetching = engine.GetMagnetMetadataWithProgressAsync(link, progress, cancellationToken: cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
    }
}

public class TorrentSurfaceTests
{
    [Fact]
    public void TheByteCountersStartAtZeroAndTrackTheTransfer()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Equal(0, torrent.DataDownloaded);
        Assert.Equal(0, torrent.DataUploaded);
    }

    [Fact]
    public void TheDiskLimitsRoundTripThroughTheTorrentsConfiguration()
    {
        // These are the per-torrent overrides of the engine-wide disk limits, and they are read on
        // every disk operation rather than at startup.
        var torrent = TorrentTestUtility.CreateMinimal();

        torrent.DiskReadLimitBytesPerSecond = 1024;
        torrent.DiskWriteLimitBytesPerSecond = 2048;

        Assert.Equal(1024, torrent.DiskReadLimitBytesPerSecond);
        Assert.Equal(2048, torrent.DiskWriteLimitBytesPerSecond);

        // Zero is the documented "unlimited", not a rejected value.
        torrent.DiskReadLimitBytesPerSecond = 0;
        Assert.Equal(0, torrent.DiskReadLimitBytesPerSecond);
    }

    [Fact]
    public void ThePeerIdIsTheEnginesAndIsTwentyBytes()
    {
        // Every handshake this torrent sends carries it, and a tracker keys its peer records on it.
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Equal(20, torrent.PeerId.Length);
        Assert.Equal(torrent.Settings.PeerId, torrent.PeerId.ToArray());
    }

    [Fact]
    public void StateTimestampIsAUtcInstantThatMovesWithActivity()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Equal(TimeSpan.Zero, torrent.StateTimestamp.Offset);
    }

    [Fact]
    public void TheStreamingViewReportsNoStreamableFilesForATorrentOfOneBinary()
    {
        // Streamability is decided by extension, so a torrent of one unnamed blob has none - and the
        // two properties have to agree with each other.
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Empty(torrent.StreamableFileIndices);
        Assert.False(torrent.HasStreamableFiles);
    }

    [Fact]
    public void TheStreamingViewFindsAFileWhoseExtensionIsPlayable()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "movie.mp4", Size = 16384 });
        metadata.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        Assert.True(torrent.HasStreamableFiles);
        Assert.Equal([0], torrent.StreamableFileIndices);
    }

    [Fact]
    public void TheLsdManagerIsAbsentUntilTheNetworkSuppliesOne()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Null(torrent.LsdManager);
    }

    [Fact]
    public async Task SettingEveryFilesPriorityAtOnceReachesEachFile()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 3;
        for (int i = 0; i < 3; i++)
        {
            metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = $"f{i}", Size = 16384 });
            metadata.Info.Pieces.Add(new byte[20]);
        }

        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        await torrent.SetAllFilesPriorityAsync(Priority.High, TestContext.Current.CancellationToken);
    }
}

public class TorrentConfigurationSurfaceTests
{
    [Fact]
    public void ThePerTorrentConnectionAndSlotLimitsRoundTrip()
    {
        // Zero means "use the engine's limit", which is why these are not clamped to one.
        var torrent = TorrentTestUtility.CreateMinimal();

        torrent.MaxConnections = 50;
        torrent.MaxUploadSlots = 8;

        Assert.Equal(50, torrent.MaxConnections);
        Assert.Equal(8, torrent.MaxUploadSlots);

        torrent.MaxConnections = 0;
        torrent.MaxUploadSlots = 0;
        Assert.Equal(0, torrent.MaxConnections);
        Assert.Equal(0, torrent.MaxUploadSlots);
    }
}

public class InfoHashMemoryTests
{
    [Fact]
    public void MemoryAndSpanShowTheSameBytes()
    {
        var hash = InfoHash.CreateRandom();

        Assert.Equal(hash.Span.ToArray(), hash.Memory.ToArray());
        Assert.Equal(20, hash.Memory.Length);
    }

    [Fact]
    public void ADefaultInfoHashHasEmptyMemoryRatherThanNull()
    {
        // Structs are always default-constructible, and a hash reaches code paths before it has been
        // resolved from a magnet.
        InfoHash hash = default;

        Assert.Equal(0, hash.Memory.Length);
    }
}

public class PeerPriorityCompareTests
{
    [Fact]
    public void AHigherPriorityValueSortsAbove()
    {
        // The unchoke ordering reads this, so an inverted comparison would prefer exactly the peers
        // it is meant to demote.
        Assert.True(PeerPriority.Compare(200, 100) > 0);
        Assert.True(PeerPriority.Compare(100, 200) < 0);
        Assert.Equal(0, PeerPriority.Compare(100, 100));
    }

    [Fact]
    public void ItComparesAsUnsignedSoTheTopOfTheRangeRanksHighest()
    {
        // Priorities come from a hash and use the whole 32-bit range. Compared as signed, everything
        // above int.MaxValue would sort below zero - which is half of all peers.
        Assert.True(PeerPriority.Compare(uint.MaxValue, 1) > 0);
        Assert.True(PeerPriority.Compare(0, uint.MaxValue) < 0);
    }
}

public class Field25519SquareRepeatedlyTests
{
    [Fact]
    public void SquaringOnceMatchesSquare()
    {
        var value = Sample();

        Assert.Equal(Bytes(value.Square()), Bytes(value.SquareRepeatedly(1)));
    }

    [Fact]
    public void SquaringNTimesMatchesNSeparateSquarings()
    {
        // The inversion Ed25519 verification depends on is written as chains of these, so an
        // off-by-one in the loop would make every signature check wrong.
        var value = Sample();

        var byHand = value;
        for (int i = 0; i < 5; i++) byHand = byHand.Square();

        Assert.Equal(Bytes(byHand), Bytes(value.SquareRepeatedly(5)));
    }

    [Fact]
    public void SquaringZeroTimesIsTheIdentity()
    {
        var value = Sample();

        Assert.Equal(Bytes(value), Bytes(value.SquareRepeatedly(0)));
    }

    private static Field25519 Sample() =>
        Field25519.FromBytes([.. Enumerable.Range(1, 32).Select(i => (byte)i)]);

    private static byte[] Bytes(Field25519 value)
    {
        byte[] buffer = new byte[32];
        value.WriteBytes(buffer);
        return buffer;
    }
}

public class TorrentPieceCheckerContextSizeTests
{
    [Fact]
    public void FullSizeIsTheTorrentsOwnTotal()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 4;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = 16384 * 4 });
        for (int i = 0; i < 4; i++) metadata.Info.Pieces.Add(new byte[20]);

        var context = new TorrentPieceCheckerContext(TorrentTestUtility.CreateMinimal(metadata));

        Assert.Equal(16384L * 4, context.FullSize);
    }
}
