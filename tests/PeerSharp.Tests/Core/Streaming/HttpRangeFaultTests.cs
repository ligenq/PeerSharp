using System.Net;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Streaming;

namespace PeerSharp.Tests.Core.Streaming;

public sealed class HttpRangeFaultTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    // The stream also prioritizes the first and last MiB. Put the read windows between them.
    private const int CancelledPiece = 1024 * 1024 / PieceSize;
    private const int SurvivingPiece = CancelledPiece + 1;
    private const int FileSize = 2 * 1024 * 1024 + 4 * PieceSize;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task CancellingOneRangePreservesTheOtherAndStopDrainsWaitingReads(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpHttpRange-");
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
        var payload = new byte[FileSize];
        scenario.Random.NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("video.mp4").WithPieceLength(PieceSize)
            .AddFile("video.mp4", payload).Build();
        await using var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, path, timeProvider: new FakeTimeProvider());
        torrent.Settings.Streaming.ReadAheadBytes = PieceSize;
        torrent.Settings.Streaming.ReadAheadSeconds = 0;
        await torrent.StartAsync(ct);

        var handler = new HttpStreamRequestHandler(torrent, 0);
        int survivorStart = SurvivingPiece * PieceSize + scenario.Random.Next(1, 128);
        int survivorEnd = survivorStart + scenario.Random.Next(128, 512);
        var survivingRange = new RangeRequest(survivorStart, survivorEnd);
        var cancelledRange = new RangeRequest(CancelledPiece * PieceSize + 11, survivorEnd - 1);

        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelReader = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
        using var cancelledBody = new MemoryStream();
        using var survivingBody = new PausedBody();
        using var stoppedBody1 = new MemoryStream();
        using var stoppedBody2 = new MemoryStream();
        var cancelledResponse = new RangeResponse(cancelledBody);
        var survivingResponse = new RangeResponse(survivingBody);
        var requests = new List<Task>();

        Task Start(RangeRequest request, RangeResponse response, CancellationToken token)
        {
            var processing = handler.ProcessAsync(request, response, token);
            requests.Add(processing);
            return processing;
        }

        Task cancelled;
        Task surviving;
        bool survivorFirst = (seed & 2) != 0;
        scenario.Record($"start the two overlapping ranges, opening the {(survivorFirst ? "surviving" : "cancelled")} reader first");
        try
        {
            if (survivorFirst)
            {
                surviving = Start(survivingRange, survivingResponse, cleanup.Token);
                cancelled = Start(cancelledRange, cancelledResponse, cancelReader.Token);
            }
            else
            {
                cancelled = Start(cancelledRange, cancelledResponse, cancelReader.Token);
                surviving = Start(survivingRange, survivingResponse, cleanup.Token);
            }

            var both = Assert.IsAssignableFrom<IReadOnlyList<int>>(torrent.StreamingPriorityPieces);
            Assert.Contains(CancelledPiece, both);
            Assert.Contains(SurvivingPiece, both);
            Assert.Contains(SurvivingPiece + 1, both);
            Assert.False(cancelled.IsCompleted);
            Assert.False(surviving.IsCompleted);

            bool verifyFirst = (seed & 1) != 0;
            scenario.Record($"cancel the missing-piece reader {(verifyFirst ? "after piece verification while the other response is writing" : "before piece verification")}");
            if (verifyFirst)
            {
                await DeliverPieceAsync(torrent, payload).WaitAsync(Timeout, ct);
                await survivingBody.Entered.Task.WaitAsync(Timeout, ct);
            }
            await cancelReader.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout, ct));
            Assert.Equal(0, cancelledBody.Length);
            Assert.False(surviving.IsCompleted);
            var remaining = Assert.IsAssignableFrom<IReadOnlyList<int>>(torrent.StreamingPriorityPieces);
            Assert.DoesNotContain(CancelledPiece, remaining);
            Assert.Contains(SurvivingPiece + 1, remaining);

            if (!verifyFirst)
            {
                await DeliverPieceAsync(torrent, payload).WaitAsync(Timeout, ct);
                await survivingBody.Entered.Task.WaitAsync(Timeout, ct);
            }
            survivingBody.Release.SetResult();
            await surviving.WaitAsync(Timeout, ct);
            Assert.Equal((int)HttpStatusCode.PartialContent, survivingResponse.StatusCode);
            Assert.Equal($"bytes {survivorStart}-{survivorEnd}/{FileSize}", survivingResponse.Headers["Content-Range"]);
            Assert.Equal(survivorEnd - survivorStart + 1, survivingResponse.ContentLength);
            Assert.Equal(payload[survivorStart..(survivorEnd + 1)], survivingBody.ToArray());
            Assert.Null(torrent.StreamingPriorityPieces);

            scenario.Record("open two more ranges blocked on missing pieces and stop the torrent with both reads pending");
            int stopStart1 = (SurvivingPiece + 1) * PieceSize + 3;
            int stopStart2 = (SurvivingPiece + 2) * PieceSize + 7;
            Task stopping1 = Start(new RangeRequest(stopStart1, stopStart1 + 10), new RangeResponse(stoppedBody1), cleanup.Token);
            Task stopping2 = Start(new RangeRequest(stopStart2, stopStart2 + 10), new RangeResponse(stoppedBody2), cleanup.Token);
            Assert.False(stopping1.IsCompleted);
            Assert.False(stopping2.IsCompleted);
            await torrent.StopAsync(ct).WaitAsync(Timeout, ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping1.WaitAsync(Timeout, ct));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping2.WaitAsync(Timeout, ct));
            Assert.Equal(0, stoppedBody1.Length);
            Assert.Equal(0, stoppedBody2.Length);
            Assert.Null(torrent.StreamingPriorityPieces);
        }
        finally
        {
            await cleanup.CancelAsync();
            survivingBody.Release.TrySetResult();
            await Task.WhenAll(requests).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static Task DeliverPieceAsync(Torrent torrent, byte[] payload)
    {
        var block = new Block(SurvivingPiece, 0, PieceSize);
        payload.AsSpan(SurvivingPiece * PieceSize, PieceSize).CopyTo(block.Buffer);
        return torrent.FileTransferInternal.WebSeedBlockReceivedAsync(block);
    }

    private sealed record RangeRequest(int Start, int End) : IHttpStreamRequest
    {
        public string Method => "GET";
        public string Path => HttpStreamRequestHandler.DefaultPath;
        public string RangeHeader => $"bytes={Start}-{End}";
        public string Host => "localhost";
    }

    private sealed class RangeResponse(Stream body) : IHttpStreamResponse
    {
        public Stream Body => body;
        public long ContentLength { get; set; }
        public string ContentType { get; set; } = "";
        public int StatusCode { get; set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void AddHeader(string name, string value) => Headers.Add(name, value);
    }

    private sealed class PausedBody : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
