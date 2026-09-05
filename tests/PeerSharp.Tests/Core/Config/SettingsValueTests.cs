using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Clients;
using PeerSharp.Config;

namespace PeerSharp.Tests.Core.Config;

public class FilesSettingsTests
{
    [Fact]
    public void DiskSpeedLimits_RejectNegativesAndTreatZeroAsUnlimited()
    {
        // Zero means unlimited throughout the engine, so a negative is not "even less" - it is a
        // value nothing downstream has a meaning for, and taking it would silently stall the disk.
        var settings = new FilesSettings();

        Assert.Equal(0, settings.MaxDiskReadSpeed);
        Assert.Equal(0, settings.MaxDiskWriteSpeed);

        settings.MaxDiskReadSpeed = 1024;
        settings.MaxDiskWriteSpeed = 2048;
        Assert.Equal(1024, settings.MaxDiskReadSpeed);
        Assert.Equal(2048, settings.MaxDiskWriteSpeed);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxDiskReadSpeed = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxDiskWriteSpeed = -1);

        // The rejected write left the previous value alone.
        Assert.Equal(1024, settings.MaxDiskReadSpeed);
        Assert.Equal(2048, settings.MaxDiskWriteSpeed);
    }

    [Fact]
    public void Defaults_AreTheOnesTheStorageLayerAssumes()
    {
        var settings = new FilesSettings();

        Assert.Equal(string.Empty, settings.DefaultDownloadPath);
        Assert.True(settings.EnableSparseFiles);
        Assert.True(settings.EnableReadAhead);
        Assert.Equal(8 * 1024 * 1024, settings.BlockCacheSizeBytes);
        Assert.Equal(16, settings.ReadAheadBlocks);
    }
}

public class AlertSettingsTests
{
    [Fact]
    public void Defaults_BoundTheQueueAndFavourRecency()
    {
        var settings = new AlertSettings();

        Assert.Equal(10000, settings.MaxQueueSize);
        Assert.Equal(AlertOverflowPolicy.DropOldest, settings.OverflowPolicy);
    }

    [Fact]
    public void MaxQueueSize_RejectsAValueThatCouldNotHoldAnything()
    {
        var settings = new AlertSettings();

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxQueueSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxQueueSize = -5);
        Assert.Equal(10000, settings.MaxQueueSize);

        settings.MaxQueueSize = 1;
        Assert.Equal(1, settings.MaxQueueSize);
    }

    [Fact]
    public void OverflowPolicy_IsSettable()
    {
        var settings = new AlertSettings { OverflowPolicy = AlertOverflowPolicy.DropNewest };
        Assert.Equal(AlertOverflowPolicy.DropNewest, settings.OverflowPolicy);
    }
}

public class TransferSettingsTests
{
    [Fact]
    public void SpeedLimits_RejectNegatives()
    {
        var settings = new TransferSettings();

        settings.MaxDownloadSpeed = 500;
        settings.MaxUploadSpeed = 250;
        Assert.Equal(500, settings.MaxDownloadSpeed);
        Assert.Equal(250, settings.MaxUploadSpeed);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxDownloadSpeed = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxUploadSpeed = -1);
        Assert.Equal(500, settings.MaxDownloadSpeed);
        Assert.Equal(250, settings.MaxUploadSpeed);
    }

    [Fact]
    public void ActivePieceByteBudget_RejectsZeroAndNegatives()
    {
        // Zero would mean no piece may be opened, which is not a limit but a stall.
        var settings = new TransferSettings();

        Assert.Equal(32L * 1024 * 1024, settings.ActivePieceByteBudget);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ActivePieceByteBudget = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ActivePieceByteBudget = -1);

        settings.ActivePieceByteBudget = 64L * 1024 * 1024;
        Assert.Equal(64L * 1024 * 1024, settings.ActivePieceByteBudget);
    }

    [Fact]
    public void ConcurrencyLimits_AreReadBackThroughTheirVolatileAccessors()
    {
        // These two are written through Volatile so a change reaches a running transfer. The point
        // of the test is that the accessor pair agrees; the notification is covered elsewhere.
        var settings = new TransferSettings();

        Assert.Equal(8, settings.MaxConcurrentPieceHashing);
        Assert.Equal(8, settings.MaxConcurrentPieceWrites);

        settings.MaxConcurrentPieceHashing = 32;
        settings.MaxConcurrentPieceWrites = 16;

        Assert.Equal(32, settings.MaxConcurrentPieceHashing);
        Assert.Equal(16, settings.MaxConcurrentPieceWrites);
    }

    [Fact]
    public void ConcurrencyChanges_ReachEveryRegisteredListenerAndStopAtDeregistration()
    {
        var settings = new TransferSettings();
        var first = new CountingListener();
        var second = new CountingListener();

        settings.AddConcurrencyLimitListener(first);
        settings.AddConcurrencyLimitListener(second);
        settings.MaxConcurrentPieceHashing = 4;

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);

        settings.RemoveConcurrencyLimitListener(first);
        settings.MaxConcurrentPieceWrites = 4;

        Assert.Equal(1, first.Calls);
        Assert.Equal(2, second.Calls);
    }

    [Fact]
    public void ConcurrencyChanges_WithNoListenersDoNotThrow()
    {
        var settings = new TransferSettings();
        settings.MaxConcurrentPieceHashing = 4;
        settings.MaxConcurrentPieceWrites = 4;
    }

    [Fact]
    public void Defaults_AreTheOnesTheSchedulerAndWebSeedsAssume()
    {
        var settings = new TransferSettings();

        Assert.Equal(500, settings.MaxRequestsPerPeer);
        Assert.Equal(3, settings.RequestQueueTimeSeconds);
        Assert.Equal(16, settings.InitialPipelineDepth);
        Assert.Equal(2, settings.WebSeedMaxConnections);
        Assert.Equal(2, settings.WebSeedMaxConnectionsPerSource);
        Assert.Equal(256, settings.MaxActivePieces);
        Assert.Equal(8, settings.MinActivePieces);
        Assert.Equal(2, settings.ActivePiecesPerPeer);
        Assert.Equal(6, settings.BlockTimeoutRttMultiplier);
        Assert.Equal(10, settings.BlockHardTimeoutRttMultiplier);
        Assert.Equal(4, settings.BlockTimeoutVarianceMultiplier);
        Assert.Equal(3000, settings.MinBlockTimeoutMs);
        Assert.Equal(15000, settings.MaxBlockTimeoutMs);
        Assert.Equal(5000, settings.MinBlockHardTimeoutMs);
        Assert.Equal(30000, settings.MaxBlockHardTimeoutMs);
    }

    private sealed class CountingListener : PeerSharp.Internals.Framework.IConcurrencyLimitListener
    {
        public int Calls { get; private set; }

        public void OnConcurrencyLimitsChanged() => Calls++;
    }
}

public class TorrentClientOptionsTests
{
    [Fact]
    public void EffectiveLoggerFactory_FallsBackToNullRatherThanThrowing()
    {
        // The library ships no logging implementation, so no logger factory is the normal case and
        // must not be a null reference waiting somewhere further in.
        Assert.Same(NullLoggerFactory.Instance, new TorrentClientOptions().EffectiveLoggerFactory);

        var provided = new NullLoggerFactory();
        Assert.Same(provided, new TorrentClientOptions { LoggerFactory = provided }.EffectiveLoggerFactory);
    }

    [Fact]
    public void TheOptionalPiecesAreNullUntilTheCallerSuppliesThem()
    {
        var options = new TorrentClientOptions();

        Assert.Null(options.LoggerFactory);
        Assert.Null(options.Settings);
        Assert.Null(options.SessionPersistence);
    }
}

public class DhtIndexerOptionsTests
{
    [Fact]
    public void Defaults_KeepTheStreamDistinctAndTheCrawlPolite()
    {
        var options = new DhtIndexerOptions();

        // Repeat sightings are an untrusted ranking hint, so they are off by default.
        Assert.False(options.ReturnDuplicateSightings);

        // Concurrency is the politeness knob as much as the throughput one.
        Assert.Equal(4, options.MaxConcurrency);

        // Bounded by default because duplicate suppression remembers every hash it has returned.
        Assert.Equal(100_000, options.MaxInfoHashes);
    }

    [Fact]
    public void MaxInfoHashes_CanBeClearedToRunUntilCancelled()
    {
        var options = new DhtIndexerOptions { MaxInfoHashes = null };
        Assert.Null(options.MaxInfoHashes);
    }
}

public class ClientEngineFactoryTests
{
    [Fact]
    public async Task Create_ReturnsAUsableEngineWithDefaultSettings()
    {
        await using var engine = ClientEngineFactory.Create();

        Assert.NotNull(engine);
        Assert.NotNull(engine.Settings);
        Assert.Empty(engine.GetTorrents());
    }

    [Fact]
    public async Task Create_UsesTheSettingsInstanceItWasGiven()
    {
        // Same instance, not a copy: everything the engine builds reads through this object, and
        // the runtime-settings contract depends on the caller still holding the one it passed in.
        var settings = new Settings();
        settings.Transfer.MaxRequestsPerPeer = 123;

        await using var engine = ClientEngineFactory.Create(new TorrentClientOptions { Settings = settings });

        Assert.Same(settings, engine.Settings);
        Assert.Equal(123, engine.Settings.Transfer.MaxRequestsPerPeer);
    }
}
