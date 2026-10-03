using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Seeding;
using System.Collections.Concurrent;
using System.Net;

namespace PeerSharp.Tests.Core.Seeding;

public class WebSeedSchedulingTests
{
    [Fact(Timeout = 15000)]
    public async Task Worker_UsesConfiguredConcurrency_AndReReadsItWhileRequestsAreBlocked()
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 1);
        torrent.Settings.Transfer.WebSeedMaxConnections = 3;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 3;
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => client.Requests.Count == 3, TimeSpan.FromSeconds(1));
        Assert.Equal(3, client.Requests.Select(r => r.Piece).Distinct().Count());
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_ReplenishesCompletedSlot_WhileAnotherSourceIsBlocked()
    {
        await using var torrent = CreateTorrent();
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file", "http://two.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => client.Requests.Count == 2, TimeSpan.FromSeconds(3));
        var first = client.Requests.First();
        torrent.Pieces.AddPiece(first.Piece);
        first.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[16384])
        });

        // No further clock advance: productive work must not wait for a polling tick or the slow source.
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 3);
        Assert.False(client.Requests.ElementAt(1).Completion.Task.IsCompleted);
        Assert.Equal(3, client.Requests.Select(r => r.Piece).Distinct().Count());
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_PipelinesOneSource_WithoutAStartupSleep()
    {
        await using var torrent = CreateTorrent();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], new FakeTimeProvider());
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 2);
        Assert.Equal(2, client.Requests.Select(r => r.Piece).Distinct().Count());
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_DoesNotDial_WhileTheHashCheckIsRunning()
    {
        await using var torrent = CreateTorrent();
        // GetNeededPieces reads HasPiece, which reports false for every piece until the check has
        // written its results. Dialling now re-fetches over HTTP what a resumed torrent already has.
        torrent.FilesInternal.Checking = true;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();

        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Empty(client.Requests);

        torrent.FilesInternal.Checking = false;
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => !client.Requests.IsEmpty, TimeSpan.FromSeconds(1));
        await manager.StopAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_SpacesRetries_AfterASourceFailsImmediately()
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        var clock = new FakeTimeProvider();
        var client = new FailingClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();

        await TorrentTestUtility.WaitUntilAsync(() => client.Calls == 1);

        // The failure completes the download task at once, so the loop wakes with a free slot and
        // work waiting. Without a retry delay it would redial in the same breath, which is a spin
        // rather than a retry: the batch-and-sleep loop this replaced was spaced by its sleep.
        await Task.Delay(250);
        Assert.Equal(1, client.Calls);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => client.Calls >= 2, TimeSpan.FromSeconds(1));
        await manager.StopAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task NeededPieces_PutWhatStreamsNeedFirst_InTheOrderTheyNeedIt()
    {
        await using var torrent = CreateTorrent();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], new FakeTimeProvider());
        using var stream = Stream(torrent, 5, 6, 2);

        Assert.Equal([5, 6, 2, 0, 1], manager.GetNeededPieces(5));
        Assert.Equal([5, 6], manager.GetUrgentPieces());

        torrent.Pieces.AddPiece(5);
        Assert.Equal([6, 2], manager.GetUrgentPieces());
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_RacesAPieceAStreamWaitsOn_FromAFasterSource_AndGivesUpTheSlowerOnceItIsHere()
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 2;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://slow.test/file", "http://fast.test/file"], clock);
        manager.SetTestClient(client);
        using var stream = Stream(torrent, 3, 4);
        manager.Start();

        // The stream's two pieces go out first, one to each source.
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 2);
        var slow = client.Requests.Single(request => request.Host == "slow.test");
        var fast = client.Requests.Single(request => request.Host == "fast.test");
        Assert.Equal(3, slow.Piece);
        Assert.Equal(4, fast.Piece);

        // The fast source sends its whole piece in the half second the slow one has had its own.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        torrent.Pieces.AddPiece(4);
        fast.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 3);
        var race = client.Requests.Last();
        Assert.Equal(("fast.test", 3), (race.Host, race.Piece));

        torrent.Pieces.AddPiece(3);
        race.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });
        await TorrentTestUtility.WaitUntilAsync(() => slow.Request.IsCanceled);
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_RacesNothing_WhileNothingStreams()
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 2;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file", "http://two.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 2);
        var second = client.Requests.Single(request => request.Piece == 1);

        clock.Advance(TimeSpan.FromSeconds(2));
        torrent.Pieces.AddPiece(1);
        second.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });

        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 3);
        Assert.Equal(2, client.Requests.Last().Piece);
        await manager.StopAsync();
    }

    [Theory(Timeout = 15000)]
    [InlineData("slow.test")]
    [InlineData("fast.test")]
    public async Task Worker_CancelsTheLosingSource_WhenVerificationFinishesAfterTheWinningHttpTask(string winnerHost)
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 2;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://slow.test/file", "http://fast.test/file"], clock);
        manager.SetTestClient(client);
        using var stream = Stream(torrent, 3, 4);
        manager.Start();

        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 2);
        var fast = client.Requests.Single(request => request.Host == "fast.test");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        torrent.Pieces.AddPiece(4);
        fast.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 3);
        var winner = client.Requests.Single(request => request.Piece == 3 && request.Host == winnerHost);
        var loser = client.Requests.Single(request => request.Piece == 3 && request.Host != winnerHost);

        // Keep the worker from replenishing slots while the piece waits in the hash/write queue.
        torrent.FilesInternal.Checking = true;
        winner.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });
        await TorrentTestUtility.WaitUntilAsync(() => manager.GetStats().ActiveDownloads == 1);

        // Allow several cleanup passes before verification; merely receiving bytes must not
        // cancel the rival, since the bytes could still fail their hash.
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100);
        }

        Assert.False(loser.Request.IsCanceled);
        torrent.Pieces.AddPiece(3);
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => loser.Request.IsCanceled, TimeSpan.FromSeconds(1), timeoutMs: 3000);
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
    }

    [Fact(Timeout = 15000)]
    public async Task Worker_CancelsAnHttpDownload_WhenPeersVerifyItsPiece()
    {
        await using var torrent = CreateTorrent();
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://one.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();

        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 1);
        var download = client.Requests.Single();
        torrent.Pieces.AddPiece(download.Piece);
        await TorrentTestUtility.AdvanceUntilAsync(clock, () => download.Request.IsCanceled, TimeSpan.FromSeconds(1), timeoutMs: 3000);
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
    }

    /// <summary>A stream open on <paramref name="torrent"/> that needs <paramref name="pieces"/>, in that order.</summary>
    private static PeerSharp.Streaming.TorrentStream Stream(Torrent torrent, params int[] pieces)
    {
        var stream = new PeerSharp.Streaming.TorrentStream(torrent.Streaming, torrent, 0, TimeProvider.System);
        torrent.Streaming.UpdatePriorities(stream, pieces);
        return stream;
    }

    private static Torrent CreateTorrent()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 8;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = metadata.Info.FullSize });
        for (int i = 0; i < 8; i++) metadata.Info.Pieces.Add(new byte[20]);
        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var selection = (TorrentTestUtility.MockFileSelectionManager)typeof(Torrent)
            .GetField("_fileSelectionManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(torrent)!;
        selection.IsSelectionFinished = false;
        return torrent;
    }

    [Fact(Timeout = 10000)]
    public async Task RepeatedStartDoesNotCreateAnotherWorker()
    {
        await using var torrent = CreateTorrent();
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        torrent.Settings.Transfer.WebSeedMaxConnectionsPerSource = 1;
        await using var manager = new WebSeedManager(torrent, ["http://seed.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 1);
        manager.Start();
        Assert.Single(client.Requests);
        await manager.StopAsync();
        Assert.Equal(0, manager.GetStats().ActiveDownloads);
        await manager.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(manager.Start);
    }

    [Fact(Timeout = 10000)]
    public async Task ASourceSupplyingAKnownBadPieceIsRetired()
    {
        await using var torrent = CreateTorrent();
        var clock = new FakeTimeProvider();
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://seed.test/file"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == 1);
        client.Requests.Single().Completion.SetResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[16384]) });
        await TorrentTestUtility.WaitUntilAsync(() => manager.GetStats().ActiveDownloads == 0);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, manager.GetStats().AvailableSources);
        Assert.Single(client.Requests);
        Assert.False(torrent.Pieces.HasPiece(0));
        await manager.StopAsync();
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2DownloadsOnlyTheContentBytesOfAnUnalignedFile(bool multipleFiles)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = multipleFiles ? 32768 : 16384;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Offset = 0, Size = 1000, FirstPieceIndex = 0, PieceCount = 1, PiecesRoot = new byte[32] });
        if (multipleFiles) metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Offset = 16384, Size = 2000, FirstPieceIndex = 1, PieceCount = 1, PiecesRoot = new byte[32] });
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        ((TorrentTestUtility.MockFileSelectionManager)typeof(Torrent).GetField("_fileSelectionManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(torrent)!).IsSelectionFinished = false;
        var clock = new FakeTimeProvider();
        var client = new ControlledClient();
        await using var manager = new WebSeedManager(torrent, ["http://seed.test/root"], clock);
        manager.SetTestClient(client);
        manager.Start();
        await TorrentTestUtility.WaitUntilAsync(() => client.Requests.Count == (multipleFiles ? 2 : 1));
        Assert.Equal(999, client.Requests.First().To);
        if (multipleFiles) Assert.Equal(1999, client.Requests.Last().To);
        await manager.StopAsync();
    }

    private sealed record Pending(int Piece, TaskCompletionSource<HttpResponseMessage> Completion, string Host, Task<HttpResponseMessage> Request, long To);

    private sealed class FailingClient : IHttpClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
        }
    }

    private sealed class ControlledClient : IHttpClient
    {
        public ConcurrentQueue<Pending> Requests { get; } = new();
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var range = request.Headers.Range!.Ranges.Single();
            var answer = AnswerAsync(completion, range.From!.Value, range.To!.Value, cancellationToken);
            Requests.Enqueue(new Pending((int)(request.Headers.Range!.Ranges.Single().From!.Value / 16384), completion, request.RequestUri!.Host, answer, range.To.Value));
            return answer;
        }

        private static async Task<HttpResponseMessage> AnswerAsync(TaskCompletionSource<HttpResponseMessage> completion, long from, long to, CancellationToken ct)
        {
            var response = await completion.Task.WaitAsync(ct);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to);
            }
            return response;
        }
    }
}

