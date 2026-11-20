using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Seeding;

namespace PeerSharp.Tests.Core.Seeding;

public sealed class WebSeedFailoverFaultTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AFailedSecondRangeIsDiscardedBeforeAnotherSourceVerifiesThePiece(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpWebSeedFault-");
        try
        {
            await RunInDirectoryAsync(root.FullName, seed, scenario, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string path, int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var payload = new byte[PieceSize];
        scenario.Random.NextBytes(payload);
        int split = scenario.Random.Next(512, PieceSize - 512);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("split").WithPieceLength(PieceSize)
            .AddFile("first.bin", payload[..split]).AddFile("second.bin", payload[split..]).Build();
        var clock = new FakeTimeProvider();
        await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, path, timeProvider: clock,
            fileSelectionManager: new FileSelectionManager(file.Metadata));
        torrent.Settings.Transfer.WebSeedMaxConnections = 1;
        await torrent.StartAsync(ct);
        int publications = 0;
        torrent.Events = new TorrentEventsBuilder().OnPieceCompleted((_, _) => Interlocked.Increment(ref publications)).Build();
        var beforePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = torrent.FileTransferInternal;
        transfer.BeforePiecePublicationForTesting = (_, token) =>
        {
            beforePublication.TrySetResult();
            return publish.Task.WaitAsync(token);
        };
        bool stall = (seed & 1) != 0;
        bool readFirst = (seed & 2) == 0;
        var client = new ScriptedRanges(payload[..split], payload[split..], stall);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var manager = new WebSeedManager(torrent, ["http://broken.test/data/"], clock);
        manager.SetTestClient(client);
        await using var first = await torrent.OpenStreamAsync(0, ct);
        await using var second = await torrent.OpenStreamAsync(1, ct);
        var actualFirst = new byte[split];
        var actualSecond = new byte[PieceSize - split];
        Task readers = Task.CompletedTask;
        void ReadBoth() => readers = Task.WhenAll(first.ReadExactlyAsync(actualFirst, cleanup.Token).AsTask(),
            second.ReadExactlyAsync(actualSecond, cleanup.Token).AsTask());

        try
        {
            if (readFirst) ReadBoth();
            scenario.Record($"return corrupt but well-formed bytes for the first range; {(stall ? "stall" : "truncate")} the second range");
            manager.Start();
            await client.BadBody.Entered.Task.WaitAsync(Timeout, ct);
            if (!readFirst) ReadBoth();
            Assert.False(readers.IsCompleted);
            Assert.False(torrent.Pieces.HasPiece(0));
            Assert.Equal(0, transfer.Downloader.Downloaded);
            Assert.True(manager.AddSource("http://healthy.test/data/"));

            if (stall)
            {
                clock.Advance(WebSeedManager.RequestTimeout);
                Assert.True(client.BadBody.ReadToken.IsCancellationRequested);
            }
            else
            {
                client.BadBody.Release.TrySetResult();
            }
            scenario.Record("let the healthy source fetch both ranges and verify the assembled piece");
            await client.HealthySecondRange.Task.WaitAsync(Timeout, ct);
            Assert.False(readers.IsCompleted);
            Assert.False(torrent.Pieces.HasPiece(0));
            Assert.Equal(0, transfer.Downloader.Downloaded);
            client.ReleaseHealthyRange.SetResult();
            await beforePublication.Task.WaitAsync(Timeout, ct);
            scenario.Record("deliver another copy after the healthy piece is written and before its bit is published");
            Assert.False(torrent.Pieces.HasPiece(0));
            Assert.False(readers.IsCompleted);
            var duplicate = new Block(0, 0, PieceSize);
            payload.CopyTo(duplicate.Buffer);
            await transfer.WebSeedBlockReceivedAsync(duplicate).WaitAsync(Timeout, ct);
            Assert.True(duplicate.Data.IsEmpty);
            Assert.Equal(PieceSize, transfer.Downloader.Downloaded);
            publish.SetResult();
            await readers.WaitAsync(Timeout, ct);
            await manager.StopAsync().WaitAsync(Timeout, ct);
            await torrent.StopAsync(ct);
            Assert.Equal(new[] { "first.bin", "second.bin" }, client.HealthyRanges.Take(2));
            Assert.Equal(payload[..split], actualFirst);
            Assert.Equal(payload[split..], actualSecond);
            Assert.True(torrent.Pieces.HasPiece(0));
            Assert.Equal(1, Volatile.Read(ref publications));
            Assert.Equal(PieceSize, transfer.Downloader.Downloaded);
            Assert.Equal(0, manager.GetStats().ActiveDownloads);
        }
        finally
        {
            await cleanup.CancelAsync();
            client.BadBody.Release.TrySetResult();
            client.ReleaseHealthyRange.TrySetResult();
            publish.TrySetResult();
            await readers.WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class ScriptedRanges(byte[] first, byte[] second, bool stall) : IHttpClient
    {
        public FailedBody BadBody { get; } = new(stall);
        public ConcurrentQueue<string> HealthyRanges { get; } = new();
        public TaskCompletionSource HealthySecondRange { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHealthyRange { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string file = Path.GetFileName(uri.AbsolutePath);
            bool healthy = uri.Host == "healthy.test";
            bool firstFile = file == "first.bin";
            byte[] data = firstFile ? first : second;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal((0L, (long)data.Length - 1), (range.From, range.To));
            if (healthy) HealthyRanges.Enqueue(file);
            if (healthy && !firstFile)
            {
                HealthySecondRange.TrySetResult();
                await ReleaseHealthyRange.Task.WaitAsync(cancellationToken);
            }
            HttpContent content = !healthy && !firstFile
                ? new StreamContent(BadBody)
                : new ByteArrayContent(healthy ? data : data.Select(value => (byte)(value ^ 0xff)).ToArray());
            content.Headers.ContentRange = new ContentRangeHeaderValue(0, data.Length - 1, data.Length);
            content.Headers.ContentLength = data.Length;
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
    }

    private sealed class FailedBody(bool stall) : MemoryStream(new byte[] { 0x42 })
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            ReadToken = cancellationToken;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return stall ? throw new OperationCanceledException(cancellationToken) : 0;
        }
    }
}
