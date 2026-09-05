using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core;

public class TorrentFileLoadTests
{
    [Fact]
    public async Task LoadingAFileFromDiskParsesTheSameTorrentAsParsingItsBytes()
    {
        // The path overload is what a consumer with a .torrent on disk calls, and it has to agree
        // with the byte overload every other test uses.
        using var directory = new ScratchDirectory();
        var built = SingleFileTorrentBytes();
        string path = Path.Combine(directory.Path, "sample.torrent");
        await File.WriteAllBytesAsync(path, built, TestContext.Current.CancellationToken);

        var loaded = await TorrentFile.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(TorrentFile.Parse(built).InfoHash, loaded.InfoHash);
    }

    [Fact]
    public async Task LoadingAPathThatIsNotThereNamesTheFileRatherThanFailingOnParse()
    {
        using var directory = new ScratchDirectory();
        string missing = Path.Combine(directory.Path, "absent.torrent");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            TorrentFile.LoadAsync(missing, TestContext.Current.CancellationToken));

        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public async Task LoadingRejectsANullPath()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TorrentFile.LoadAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadingAFileThatIsNotATorrentIsReportedAsSuch()
    {
        // A user picking the wrong file is the ordinary mistake here, and the parse failure is what
        // tells them so.
        using var directory = new ScratchDirectory();
        string path = Path.Combine(directory.Path, "notatorrent.torrent");
        await File.WriteAllTextAsync(path, "this is not bencode", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            TorrentFile.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AV1TorrentIsNotMerkle()
    {
        // Merkle means BEP 52 per-file hash trees rather than the flat v1 piece list, and the
        // checker takes an entirely different path for one.
        var torrent = TorrentFile.Parse(SingleFileTorrentBytes());

        Assert.False(torrent.IsMerkle);
    }

    /// <summary>The smallest well-formed single-file v1 torrent.</summary>
    private static byte[] SingleFileTorrentBytes()
    {
        var info = new PeerSharp.BEncoding.BDict();
        info.Dict["name"] = new PeerSharp.BEncoding.BString("sample.bin"u8.ToArray());
        info.Dict["piece length"] = new PeerSharp.BEncoding.BNumber(16384);
        info.Dict["length"] = new PeerSharp.BEncoding.BNumber(16384);
        info.Dict["pieces"] = new PeerSharp.BEncoding.BString(new byte[20]);

        var root = new PeerSharp.BEncoding.BDict();
        root.Dict["announce"] = new PeerSharp.BEncoding.BString("http://tracker.example/announce"u8.ToArray());
        root.Dict["info"] = info;

        return PeerSharp.BEncoding.BencodeWriter.Write(root);
    }
}

public class MerkleHashRequestSelectionFactoryTests
{
    [Fact]
    public void Selected_CarriesThePeerToAskAndTheKeyToRecordAgainst()
    {
        var peer = new object();

        var selection = MerkleHashRequestSelection<object>.Selected(peer, "piece-3");

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, selection.Status);
        Assert.Same(peer, selection.Peer);
        Assert.Equal("piece-3", selection.RequestKey);
    }

    [Fact]
    public void Throttled_CarriesTheKeyButNoPeer()
    {
        // The distinction the caller acts on: throttled means the request is already in flight, so
        // there is a key to wait on but nobody new to ask.
        var selection = MerkleHashRequestSelection<object>.Throttled("piece-3");

        Assert.Equal(MerkleHashRequestSelectionStatus.Throttled, selection.Status);
        Assert.Null(selection.Peer);
        Assert.Equal("piece-3", selection.RequestKey);
    }

    [Fact]
    public void Selected_AcceptsNoRequestKeyForAPeerAskedWithoutOne()
    {
        var selection = MerkleHashRequestSelection<object>.Selected(new object(), null);

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, selection.Status);
        Assert.Null(selection.RequestKey);
    }
}

public class AlertsManagerStreamTests
{
    [Fact]
    public async Task TheStreamYieldsWhatWasQueuedAndThenWaits()
    {
        var clock = new FakeTimeProvider();
        var alerts = new AlertsManager(clock);
        alerts.RegisterAlerts(uint.MaxValue);
        alerts.ConfigAlert(AlertId.ConfigChanged, "Transfer");

        using var cts = new CancellationTokenSource();
        var enumerator = alerts
            .GetAlertsAsync(TimeSpan.FromMilliseconds(10), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(AlertId.ConfigChanged, enumerator.Current.Id);

        // Nothing left, so the next step waits on the poll interval rather than ending.
        var waiting = enumerator.MoveNextAsync();
        Assert.False(waiting.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public async Task AnAlertPostedWhileTheStreamIsWaitingIsDelivered()
    {
        // The stream is how a consumer follows a running engine, so an alert raised after they
        // started reading has to reach them without another call.
        var alerts = new AlertsManager(TimeProvider.System);
        alerts.RegisterAlerts(uint.MaxValue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = alerts
            .GetAlertsAsync(TimeSpan.FromMilliseconds(5), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        var moving = enumerator.MoveNextAsync();
        alerts.ConfigAlert(AlertId.ConfigChanged, "Connection");

        Assert.True(await moving);
        Assert.Equal(AlertId.ConfigChanged, enumerator.Current.Id);
    }

    [Fact]
    public async Task CancellingAlwaysThrowsEvenWhenAlertsAreWaiting()
    {
        // Documented deliberately: completing gracefully when the queue happened to be non-empty
        // would make the same call end two different ways.
        var alerts = new AlertsManager(TimeProvider.System);
        alerts.RegisterAlerts(uint.MaxValue);
        alerts.ConfigAlert(AlertId.ConfigChanged, "Transfer");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in alerts.GetAlertsAsync(cancellationToken: cts.Token))
            {
                // Nothing: the enumeration must not survive an already-cancelled token.
            }
        });
    }
}

public class DhtManagerScrapeTests
{
    [Fact]
    public async Task ScrapingBeforeTheManagerRunsIsANoOpRatherThanAnError()
    {
        // A scrape is fired from a torrent's announce loop, which can start before the DHT does.
        await using var dht = CreateManager();

        dht.ScrapeInfoHash(InfoHash.CreateRandom());
    }

    [Fact]
    public async Task ScrapingADisposedManagerSaysSo()
    {
        var dht = CreateManager();
        await dht.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => dht.ScrapeInfoHash(InfoHash.CreateRandom()));
    }

    /// <summary>A listener that accepts every send and delivers nothing.</summary>
    private sealed class ScrapeUdpListener : PeerSharp.Internals.Network.IUdpListener
    {
        public int Port => 0;
        public void RegisterReceiver(PeerSharp.Internals.Network.IUdpReceiver receiver) { }
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DhtManager CreateManager()
    {
        var settings = new PeerSharp.Config.Settings();
        settings.Dht.Enabled = true;

        return DhtManager.Create(
            ProtocolConstants.GeneratePeerId(),
            new ScrapeUdpListener(),
            settings,
            new FakeTimeProvider(),
            null,
            new PeerSharp.Internals.Network.SystemDnsResolver(),
            NullLoggerFactory.Instance);
    }
}

public class FileTransferPickerBridgeTests
{
    [Fact]
    public void TheAvailabilityAndSelectionCallsReachThePickerWithoutThrowing()
    {
        // These are the peer manager's and the file-selection layer's way into the picker. Each is
        // a one-line forward, and the thing worth pinning is that a torrent with no peers and no
        // selection change still survives being told about one.
        var torrent = TorrentTestUtility.CreateMinimal();
        var transfer = torrent.FileTransferInternal;

        transfer.DecrementAvailability(0);
        transfer.InvalidateSelection();
        transfer.PiecesAvailabilityChanged();
        transfer.RefreshSelection();
    }

    [Fact]
    public void DecrementingAvailabilityForAPieceOutsideTheTorrentIsSurvived()
    {
        // The index comes from a departing peer's bitfield, which a peer controls.
        var torrent = TorrentTestUtility.CreateMinimal();

        torrent.FileTransferInternal.DecrementAvailability(9999);
        torrent.FileTransferInternal.DecrementAvailability(-1);
    }
}

public class PeerManagerUploadOnlyTests
{
    [Fact]
    public async Task AnnouncingUploadOnlyWithNoPeersConnectedIsANoOp()
    {
        // Called the moment metadata resolves, which for a magnet can be before any peer has
        // completed its extended handshake.
        var torrent = TorrentTestUtility.CreateMinimal();
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor());

        await manager.AnnounceUploadOnlyAsync();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task EveryConnectedPeerIsAttemptedEvenThoughNoneOfThemCanBeReached()
    {
        // None of these peers has a connection, so every refresh fails. A peer that has gone away
        // needs no announcement and the rest still do, so the loop has to step over each failure
        // rather than abandoning the peers behind it.
        var torrent = TorrentTestUtility.CreateMinimal();
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor());

        for (int i = 0; i < 3; i++)
        {
            manager.AddConnectedPeerForTesting(new PeerCommunication(
                torrent,
                new PeerSharp.Tests.Core.Transfers.NullPeerListener(),
                TimeProvider.System)
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40000 + i)
            });
        }

        await manager.AnnounceUploadOnlyAsync();

        await manager.DisposeAsync();
    }
}
