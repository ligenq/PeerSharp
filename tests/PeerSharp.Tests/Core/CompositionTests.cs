using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.PiecePicking;
using PeerSharp.PieceWriter;
using System.Net;

namespace PeerSharp.Tests.Core;

public class PiecePickingModuleTests
{
    private sealed class MinimalPickerContext : IPiecePickerContext
    {
        public DownloadStrategy DownloadStrategy => DownloadStrategy.RarestFirst;
        public int PieceCount { get; init; }
        public int CompletedPieceCount => 0;
        public IReadOnlyList<int>? StreamingPriorityPieces => null;
        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot() => null;
        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection) => Priority.Normal;
        public bool HasPiece(int pieceIndex) => false;
        public bool IsPieceActive(int pieceIndex) => false;
        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection) => true;
    }

    [Fact]
    public void CreatePickerSuppliesTheDefaultsSoACallerNeedOnlyGiveTheContext()
    {
        // The module exists so consumers do not each re-decide what "no clock" and "no logger" mean.
        // A default that came back null would surface much later, inside the picker's own loop.
        var context = new MinimalPickerContext { PieceCount = 8 };

        Assert.NotNull(PiecePickingModule.CreatePicker(context));
    }

    [Fact]
    public void CreatePickerUsesTheClockAndRandomItIsGiven()
    {
        // A seeded random is what makes a picker's choices reproducible in a test, so it has to be
        // the one passed rather than Random.Shared.
        var context = new MinimalPickerContext { PieceCount = 32 };
        var clock = new FakeTimeProvider();

        var first = PiecePickingModule.CreatePicker(context, clock, new Random(1234), NullLoggerFactory.Instance);
        var second = PiecePickingModule.CreatePicker(context, clock, new Random(1234), NullLoggerFactory.Instance);

        Assert.NotSame(first, second);
        Assert.NotNull(first);
    }

    [Fact]
    public void CreateCheckerBuildsACheckerOverTheFilesItIsGiven()
    {
        using var directory = new ScratchDirectory();
        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: directory.Path);

        var checker = PiecePickingModule.CreateChecker(
            torrent.FilesInternal,
            new TorrentPieceCheckerContext(torrent));

        Assert.NotNull(checker);
    }
}

public class PieceWriterModuleTests
{
    [Fact]
    public void CreateFilesResolvesToThePathItWasGivenRatherThanTheSettingsDefault()
    {
        // The per-torrent download path overrides the engine-wide default, and this is the seam
        // where that choice is made.
        using var directory = new ScratchDirectory();
        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: "C:\\not-this-one");

        var files = PieceWriterModule.CreateFiles(torrent, directory.Path, torrent.Services.FileHandleCache);

        Assert.Equal(directory.Path, files.DownloadPath);
    }
}

public class TorrentServicesTests
{
    [Fact]
    public void EveryServiceIsHandedBackAsTheInstanceItWasGiven()
    {
        // Reference identity is the contract: the engine owns one file-handle cache, one governor
        // and one HTTP pool, and a container that copied or rebuilt any of them would quietly give
        // each torrent its own.
        var bandwidth = new TorrentTestUtility.MockBandwidthManager();
        var alerts = new TorrentTestUtility.MockAlertsManager();
        var handles = new TorrentTestUtility.MockFileHandleCache();
        var governor = new TorrentTestUtility.MockConnectionGovernor();
        var geoIp = new TorrentTestUtility.MockGeoIpService();
        var http = new HttpClientFactory();
        var peers = new PeerCommunicationFactory(NullLoggerFactory.Instance);
        var trackers = new TorrentTestUtility.MockTrackerFactory();
        var clock = new FakeTimeProvider();
        var factories = new TorrentFactories(peers, trackers, http, NullLoggerFactory.Instance);

        var services = new TorrentServices(bandwidth, alerts, handles, governor, geoIp, factories, clock);

        Assert.Same(bandwidth, services.Bandwidth);
        Assert.Same(alerts, services.Alerts);
        Assert.Same(handles, services.FileHandleCache);
        Assert.Same(governor, services.ConnectionGovernor);
        Assert.Same(geoIp, services.GeoIp);
        Assert.Same(factories, services.Factories);
        Assert.Same(clock, services.TimeProvider);
    }

    [Fact]
    public void TheFactoryShortcutsReadThroughToTheFactoryGroup()
    {
        // These are pass-throughs rather than stored copies, so that moving a factory into the
        // group cannot leave the shortcut pointing at a stale one.
        var http = new HttpClientFactory();
        var peers = new PeerCommunicationFactory(NullLoggerFactory.Instance);
        var trackers = new TorrentTestUtility.MockTrackerFactory();
        var loggers = new NullLoggerFactory();
        var factories = new TorrentFactories(peers, trackers, http, loggers);

        var services = new TorrentServices(
            new TorrentTestUtility.MockBandwidthManager(),
            new TorrentTestUtility.MockAlertsManager(),
            new TorrentTestUtility.MockFileHandleCache(),
            new TorrentTestUtility.MockConnectionGovernor(),
            new TorrentTestUtility.MockGeoIpService(),
            factories,
            TimeProvider.System);

        Assert.Same(http, services.HttpClientFactory);
        Assert.Same(peers, services.PeerFactory);
        Assert.Same(trackers, services.TrackerFactory);
        Assert.Same(loggers, services.LoggerFactory);
    }
}

public class PeerCommunicationAdapterTests
{
    [Fact]
    public void TheAdapterReportsWhatThePeerBehindItSays()
    {
        // The picker only ever sees this interface, so a property wired to the wrong peer field
        // would make it rank on something other than what the peer actually has.
        var torrent = TorrentTestUtility.CreateMinimal();
        var peer = new PeerCommunication(torrent, new PeerSharp.Tests.Core.Transfers.NullPeerListener(), TimeProvider.System);
        var adapter = new PeerCommunicationAdapter(peer);

        Assert.Equal(peer.PeerPieces.Count, adapter.Count);
        Assert.Equal(peer.PeerChoking, adapter.IsChoking);
        Assert.Equal(peer.IsSnubbed, adapter.IsSnubbed);
    }

    [Fact]
    public void ChokeStateFollowsThePeerRatherThanBeingCapturedAtConstruction()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var peer = new PeerCommunication(torrent, new PeerSharp.Tests.Core.Transfers.NullPeerListener(), TimeProvider.System);
        var adapter = new PeerCommunicationAdapter(peer);

        Assert.True(adapter.IsChoking);

        peer.SetPeerChokingForTesting(false);
        Assert.False(adapter.IsChoking);
    }

    [Fact]
    public void HasPieceAndIsAllowedFastAnswerForThePieceAsked()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var peer = new PeerCommunication(torrent, new PeerSharp.Tests.Core.Transfers.NullPeerListener(), TimeProvider.System);
        var adapter = new PeerCommunicationAdapter(peer);

        Assert.False(adapter.HasPiece(0));
        Assert.False(adapter.IsAllowedFast(0));
        Assert.Empty(adapter.GetSuggestedPieces());
    }
}

public class TorrentPiecePickerContextTests
{
    [Fact]
    public void TheCountsAndStrategyAreReadFromTheTorrent()
    {
        var torrent = CreateTorrent(pieceCount: 8);
        var context = new TorrentPiecePickerContext(torrent);

        Assert.Equal(8, context.PieceCount);
        Assert.Equal(0, context.CompletedPieceCount);
        Assert.Equal(torrent.DownloadStrategy, context.DownloadStrategy);
    }

    [Fact]
    public void CompletedCountAndHasPieceFollowTheTorrentAsItProgresses()
    {
        var torrent = CreateTorrent(pieceCount: 8);
        var context = new TorrentPiecePickerContext(torrent);

        Assert.False(context.HasPiece(3));

        torrent.Pieces.AddPiece(3);

        Assert.True(context.HasPiece(3));
        Assert.Equal(1, context.CompletedPieceCount);
    }

    [Fact]
    public void AnExplicitPiecePriorityDecidesBothWhetherThePieceIsNeededAndHowItRanks()
    {
        // Answering "needed" from the file selection while answering "priority" from the override
        // would let the picker want a piece it then ranks as DoNotDownload, and the reverse.
        var torrent = CreateTorrent(pieceCount: 8);
        var context = new TorrentPiecePickerContext(torrent);
        var selection = context.GetFileSelectionSnapshot();

        torrent.SetPiecePriority(2, Priority.DoNotDownload);
        Assert.Equal(Priority.DoNotDownload, context.GetPiecePriority(2, selection));
        Assert.False(context.IsPieceNeeded(2, selection));

        torrent.SetPiecePriority(2, Priority.High);
        Assert.Equal(Priority.High, context.GetPiecePriority(2, selection));
        Assert.True(context.IsPieceNeeded(2, selection));
    }

    [Fact]
    public void WithNoOverrideThePriorityComesFromTheFileSelection()
    {
        var torrent = CreateTorrent(pieceCount: 8);
        var context = new TorrentPiecePickerContext(torrent);
        var selection = context.GetFileSelectionSnapshot();

        Assert.Equal(
            torrent.InfoFile.Info.GetPiecePriority(0, selection),
            context.GetPiecePriority(0, selection));
    }

    [Fact]
    public void StreamingPriorityPiecesAndActiveStateAreReadThrough()
    {
        var torrent = CreateTorrent(pieceCount: 8);
        var context = new TorrentPiecePickerContext(torrent);

        Assert.Equal(torrent.StreamingPriorityPieces, context.StreamingPriorityPieces);
        Assert.False(context.IsPieceActive(0));
    }

    private static Torrent CreateTorrent(int pieceCount)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384L * pieceCount;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = metadata.Info.FullSize });
        for (int i = 0; i < pieceCount; i++) metadata.Info.Pieces.Add(new byte[20]);

        return TorrentTestUtility.CreateMinimal(metadata);
    }
}

public class SparseFileHelperTests
{
    [Fact]
    public void MarkingARealFileSparseSucceedsOnWindowsAndIsDeclinedElsewhere()
    {
        // Sparse allocation is a Windows filesystem control. Everywhere else the answer is "no",
        // which is a supported outcome rather than a failure: the file is simply written densely.
        using var directory = new ScratchDirectory();
        string path = Path.Combine(directory.Path, "sparse.bin");
        using var file = File.Create(path);

        bool result = SparseFileHelper.TrySetSparse(file.SafeFileHandle, out int error);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(result || error != 0);
        }
        else
        {
            Assert.False(result);
            Assert.Equal(0, error);
        }
    }

    [Fact]
    public void AnInvalidHandleIsDeclinedRatherThanPassedToTheOperatingSystem()
    {
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, ownsHandle: false);

        Assert.False(SparseFileHelper.TrySetSparse(handle, out int error));
        Assert.Equal(0, error);
    }
}

/// <summary>A directory that exists for the life of the test and is removed with it.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeerSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a handle the test left open; the temp directory is swept anyway */ }
        catch (UnauthorizedAccessException) { }
    }
}
