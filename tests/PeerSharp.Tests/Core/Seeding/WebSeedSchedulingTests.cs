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

    private sealed record Pending(int Piece, TaskCompletionSource<HttpResponseMessage> Completion);

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
            Requests.Enqueue(new Pending((int)(request.Headers.Range!.Ranges.Single().From!.Value / 16384), completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }
}

