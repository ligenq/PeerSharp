using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Clients;
using PeerSharp.Tests.Integration;
using System.Diagnostics;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Streaming from a real swarm, with each peer given a longer or a shorter queue of requests: how long
/// a player waits after opening a file part-way in, and after a seek, and what the download as a whole
/// gets done meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// A peer answers requests in the order they came, so a request for what the player needs next waits
/// behind whatever the peer was already asked for - as much as <c>RequestQueueTimeSeconds</c> of it,
/// three seconds by default, libtorrent's. A shorter queue lets urgent requests through sooner; it can
/// also leave a distant peer idle between one batch of requests and the next. Over loopback, where a
/// round trip is nothing, one second took a third of a second off a seek; only a real swarm, with real
/// round trips, can say what it costs.
/// </para>
/// <para>
/// Runs alternate between the queue lengths, so each meets the swarm as the other did. Each is a fresh
/// engine with its own empty copy of the torrent, given ten seconds to join the swarm and fill its
/// peers' queues before the stream opens - a third of the way in - then plays; then seeks three quarters
/// in and plays again. Gated like the soak tests: <c>PEERSHARP_SOAK=1</c> and
/// <c>PEERSHARP_SOAK_TORRENT</c> or <c>PEERSHARP_SOAK_MAGNET</c>, content you have the right to share.
/// <c>PEERSHARP_SOAK_RATE_BYTES</c> caps each run's download (default 4 MiB/s: faster, and a small image is
/// downloaded by the time the player seeks, which then measures nothing),
/// <c>PEERSHARP_STREAMING_BITRATE</c> sets the player's rate in bytes a second (default 1 MiB/s),
/// <c>PEERSHARP_STREAMING_QUEUES</c> the queue lengths to compare (default <c>3;1</c>; <c>3/1</c> is three
/// seconds ordinarily and one while the stream buffers) and
/// <c>PEERSHARP_STREAMING_FROM_START=1</c> plays the file from its start instead, as a film is watched, for
/// <c>PEERSHARP_STREAMING_SECONDS</c> of film, with no download cap unless the rate is set, and reports
/// every stall: where in the film it came, how long it lasted, and the offset it waited on.
/// <c>PEERSHARP_STREAMING_ROUNDS</c> how many times each is run (default 3). Every run pulls some
/// hundreds of megabytes.
/// </para>
/// </remarks>
public sealed class RealSwarmStreamingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan JoinTime = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 3_600_000)]
    public async Task Streaming_WithShorterAndLongerRequestQueues()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_SOAK")))
        {
            Assert.Skip("Set PEERSHARP_SOAK=1 and PEERSHARP_SOAK_TORRENT to stream from a real swarm.");
        }

        var ct = TestContext.Current.CancellationToken;
        var source = await ResolveTorrentAsync(ct);
        int bitrate = FromEnvironment("PEERSHARP_STREAMING_BITRATE", 1024 * 1024);
        bool fromStart = Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_FROM_START") == "1";
        // Zero is no cap, which is what the from-the-start runs default to, as a streaming app runs.
        int rate = int.TryParse(Environment.GetEnvironmentVariable("PEERSHARP_SOAK_RATE_BYTES"), out int configuredRate) && configuredRate >= 0
            ? configuredRate
            : fromStart ? 0 : 4 * 1024 * 1024;
        int rounds = FromEnvironment("PEERSHARP_STREAMING_ROUNDS", 3);
        int seconds = FromEnvironment("PEERSHARP_STREAMING_SECONDS", 20);
        var queues = (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_QUEUES") ?? "3;1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var players = (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_PLAYERS") ?? "strict")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Equals("exoplayer", StringComparison.OrdinalIgnoreCase) ? StreamingSwarmTests.Player.ExoPlayer : StreamingSwarmTests.Player.Strict)
            .ToList();

        for (int round = 1; round <= rounds; round++)
        {
            foreach (string queueSeconds in queues)
            {
                foreach (var player in players)
                {
                    // PEERSHARP_STREAMING_LOG names where each run's engine logs in detail, the run appended to the name.
                    string? log = Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_LOG") is { Length: > 0 } logBase
                        ? $"{logBase}-{round}-{queueSeconds.Replace('/', '-')}s-{player.Name.Replace(' ', '-')}.log"
                        : null;
                    if (fromStart)
                    {
                        await RunFromStartAsync(source, queueSeconds, bitrate, rate, seconds, player, log, $"round {round}, queue {queueSeconds}s, {player.Name}", ct);
                        continue;
                    }

                    var run = await RunAsync(source, queueSeconds, bitrate, rate, seconds, player, log, ct);
                    Report($"round {round}, queue {queueSeconds}s, {player.Name}", run);
                }
            }
        }
    }

    /// <param name="queueSeconds">
    /// The request queue in seconds, <c>3</c>, for the whole run; or <c>3/1</c>, the first ordinarily and
    /// the second while the stream is buffering.
    /// </param>
    private async Task<Run> RunAsync(
        TorrentFile source, string queueSeconds, int bitrate, int rate, int seconds, StreamingSwarmTests.Player player, string? log, CancellationToken ct)
    {
        var settings = new Settings();
        settings.Transfer.MaxDownloadSpeed = (uint)rate;
        settings.Transfer.MaxUploadSpeed = (uint)rate;
        var queue = queueSeconds.Split('/');
        settings.Transfer.RequestQueueTimeSeconds = int.Parse(queue[0], System.Globalization.CultureInfo.InvariantCulture);
        settings.Streaming.RequestQueueSecondsWhileBuffering = queue.Length > 1 ? int.Parse(queue[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        string downloadPath = Path.Combine(Path.GetTempPath(), "peersharp-streaming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);
        settings.Files.DefaultDownloadPath = downloadPath;

        using var logging = log is null
            ? null
            : LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new TimestampedFileLoggerProvider(log)));
        var engine = ClientEngineFactory.Create(new TorrentClientOptions { Settings = settings, LoggerFactory = logging ?? NullLoggerFactory.Instance });
        try
        {
            await engine.InitializeAsync(ct);
            var torrent = await engine.AddTorrentAsync(source, new AddTorrentOptions(), ct);
            if (torrent.State == TorrentState.Stopped)
            {
                await torrent.StartAsync(ct);
            }

            await Task.Delay(JoinTime, ct);
            int peers = torrent.Peers.ConnectedCount;
            long before = (long)torrent.FinishedBytes;
            var clock = Stopwatch.StartNew();

            // The file is played as a film of the given rate; the largest file, where a torrent has several.
            var largest = torrent.GetAllFileInfo().MaxBy(info => info.Size)!;
            int file = largest.Index;
            long size = largest.Size;
            await using var stream = await torrent.OpenStreamAsync(file, ct);
            var resumed = await StreamingSwarmTests.PlayAsync(stream, null, size / 3, ct, bitrate, seconds * bitrate, player);
            var afterSeek = await StreamingSwarmTests.PlayAsync(stream, null, size / 4 * 3, ct, bitrate, seconds * bitrate, player);

            double throughput = ((long)torrent.FinishedBytes - before) / clock.Elapsed.TotalSeconds;
            await torrent.StopAsync(ct);
            return new Run(peers, resumed, afterSeek, throughput);
        }
        finally
        {
            await engine.DisposeAsync();
            try
            {
                Directory.Delete(downloadPath, recursive: true);
            }
            catch (IOException) { /* Best effort. */ }
            catch (UnauthorizedAccessException) { /* Best effort. */ }
        }
    }

    /// <summary>
    /// Plays the largest file from its start for <paramref name="seconds"/> of film, as it is watched, and
    /// reports each stall. The engine is the one streaming apps use: no rate cap unless one is given.
    /// </summary>
    private async Task RunFromStartAsync(
        TorrentFile source, string queueSeconds, int bitrate, int rate, int seconds, StreamingSwarmTests.Player player, string? log, string what, CancellationToken ct)
    {
        var settings = new Settings();
        settings.Transfer.MaxDownloadSpeed = (uint)rate;
        var queue = queueSeconds.Split('/');
        settings.Transfer.RequestQueueTimeSeconds = int.Parse(queue[0], System.Globalization.CultureInfo.InvariantCulture);
        settings.Streaming.RequestQueueSecondsWhileBuffering = queue.Length > 1 ? int.Parse(queue[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        string downloadPath = Path.Combine(Path.GetTempPath(), "peersharp-streaming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);
        settings.Files.DefaultDownloadPath = downloadPath;

        using var logging = log is null
            ? null
            : LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new TimestampedFileLoggerProvider(log)));
        var logger = logging?.CreateLogger("Harness");
        var engine = ClientEngineFactory.Create(new TorrentClientOptions { Settings = settings, LoggerFactory = logging ?? NullLoggerFactory.Instance });
        try
        {
            await engine.InitializeAsync(ct);
            var torrent = await engine.AddTorrentAsync(source, new AddTorrentOptions(), ct);
            if (torrent.State == TorrentState.Stopped)
            {
                await torrent.StartAsync(ct);
            }

            await Task.Delay(JoinTime, ct);
            int peers = torrent.Peers.ConnectedCount;
            var largest = torrent.GetAllFileInfo().MaxBy(info => info.Size)!;
            long length = Math.Min(largest.Size, (long)seconds * bitrate);
            var stalls = new List<string>();
            var clock = Stopwatch.StartNew();
            await using var stream = await torrent.OpenStreamAsync(largest.Index, ct);
            var played = await StreamingSwarmTests.PlayAsync(stream, null, 0, ct, bitrate, length, player, (at, lasted, offset) =>
            {
                stalls.Add($"{(int)at.TotalMinutes}:{at.Seconds:00} ({lasted.TotalSeconds:F1}s, offset {offset})");
                logger?.LogWarning("HARNESS STALL at film {FilmTime} for {Seconds:F1}s waiting on offset {Offset} with {Peers} peers, {Speed} B/s",
                    at, lasted.TotalSeconds, offset, torrent.Peers.ConnectedCount, torrent.GetTransferStats().DownloadSpeed);
            });

            string line = $"{what}, from the start: {peers} peers; playing after {played.StartedPlaying.TotalSeconds:F2}s, " +
                $"{played.Stalls} stalls ({played.Stalled.TotalSeconds:F1}s) over {length / bitrate}s of film" +
                (stalls.Count > 0 ? ": " + string.Join(", ", stalls) : string.Empty) +
                $"; downloaded {(long)torrent.FinishedBytes / clock.Elapsed.TotalSeconds / 1024 / 1024:F1} MiB/s";
            output.WriteLine(line);
            if (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_REPORT") is { Length: > 0 } report)
            {
                File.AppendAllText(report, line + Environment.NewLine);
            }

            await torrent.StopAsync(ct);
        }
        finally
        {
            await engine.DisposeAsync();
            try
            {
                Directory.Delete(downloadPath, recursive: true);
            }
            catch (IOException) { /* Best effort. */ }
            catch (UnauthorizedAccessException) { /* Best effort. */ }
        }
    }

    private void Report(string what, Run run)
    {
        string line =
            $"{what}: {run.Peers} peers; resuming a third in, playing after {run.Resumed.StartedPlaying.TotalSeconds:F2}s, " +
            $"{run.Resumed.Stalls} stalls ({run.Resumed.Stalled.TotalSeconds:F1}s); after a seek, playing after " +
            $"{run.AfterSeek.StartedPlaying.TotalSeconds:F2}s, {run.AfterSeek.Stalls} stalls ({run.AfterSeek.Stalled.TotalSeconds:F1}s); " +
            $"downloaded {run.BytesPerSecond / 1024 / 1024:F1} MiB/s";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }

    private static async Task<TorrentFile> ResolveTorrentAsync(CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable("PEERSHARP_SOAK_TORRENT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (configured is null)
        {
            Assert.Skip("Set PEERSHARP_SOAK_TORRENT to a .torrent path or URL of content you have the right to share.");
        }

        if (configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            return TorrentFile.Parse(await http.GetByteArrayAsync(configured, ct));
        }

        return TorrentFile.Parse(await File.ReadAllBytesAsync(configured, ct));
    }

    private static int FromEnvironment(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int value) && value > 0 ? value : fallback;

    private readonly record struct Run(int Peers, StreamingSwarmTests.Playback Resumed, StreamingSwarmTests.Playback AfterSeek, double BytesPerSecond);
}
