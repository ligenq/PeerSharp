using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Net;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// How well a stream keeps a player fed from a mixed local swarm: one fast seeder and several slow
/// ones, as a real swarm has - the fast one twice as fast as the film plays. A player reads at a steady bitrate; what is measured is how long it
/// waits for the first byte, and how often and for how long it stalls after that - including after a
/// seek.
/// </summary>
/// <remarks>
/// <para>
/// The slow seeders are what make this a streaming test rather than a throughput one. The fast seeder
/// alone could keep up with the player twice over; a stall happens only when the bytes the
/// player needs next are left waiting on a slow peer while the fast one is busy with pieces nobody
/// needs yet.
/// </para>
/// <para>
/// A diagnostic, not a gate: it runs for most of a minute, on timing that a loaded build machine
/// would skew. It runs with <c>PEERSHARP_STREAMING=1</c> and reports its numbers - to the file named
/// by <c>PEERSHARP_STREAMING_REPORT</c>, when set; the assertions only catch a run that measured nothing.
/// </para>
/// </remarks>
[Collection("Integration")]
public sealed class StreamingSwarmTests : IDisposable
{
    private const int PieceLength = 2 * 1024 * 1024;
    private const int PayloadBytes = 160 * 1024 * 1024;

    /// <summary>What the player plays at: a high-bitrate film, 16 Mbit/s.</summary>
    private const int BitrateBytesPerSecond = 2 * 1024 * 1024;

    /// <summary>
    /// How much it plays from the start, and after the seek: little enough that the seek lands where
    /// nothing has downloaded yet.
    /// </summary>
    private const int PlayedBytes = 16 * 1024 * 1024;

    /// <summary>What the player buffers before it starts, again after each stall, and keeps ahead while playing: a second.</summary>
    private const int StartupBufferBytes = BitrateBytesPerSecond;

    private const int ReadBytes = 64 * 1024;
    private const int FastSeederBytesPerSecond = 4 * 1024 * 1024;
    private const int SlowSeederBytesPerSecond = 96 * 1024;
    private const int SlowSeeders = 3;

    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpStreaming_" + Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    private readonly ITestOutputHelper _output;

    public StreamingSwarmTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_testRoot);
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("PEERSHARP_STREAMING") == "1";

    [Fact(Timeout = 300000)]
    public async Task APlayer_IsKeptFed_FromAFastSeederAmongSlowOnes()
    {
        Assert.SkipUnless(Enabled, "Set PEERSHARP_STREAMING=1 to measure streaming against a local swarm.");
        var ct = TestContext.Current.CancellationToken;

        byte[] payload = new byte[PayloadBytes];
        new Random(7).NextBytes(payload);
        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("film.mkv")
            .WithPieceLength(PieceLength)
            .AddFile("film.mkv", payload)
            .Build();

        var seeders = new List<ClientEngine>();
        try
        {
            seeders.Add(await StartSeederAsync(torrentFile, payload, "fast", FastSeederBytesPerSecond));
            for (int i = 0; i < SlowSeeders; i++)
            {
                seeders.Add(await StartSeederAsync(torrentFile, payload, $"slow{i}", SlowSeederBytesPerSecond));
            }

            // PEERSHARP_STREAMING_LOG names a file the player's engine logs to, in detail, for a run to look into.
            using var leechLog = Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_LOG") is { Length: > 0 } logPath
                ? LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new PeerSharp.Tests.Interop.TimestampedFileLoggerProvider(logPath)))
                : null;
            await using var leechEngine = await CreateEngineAsync(Path.Combine(_testRoot, "leech"), leechLog);
            var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
            await ConnectAsync(leechEngine, leech, seeders, ct);

            await using var stream = await leech.OpenStreamAsync(0, ct);
            var fromStart = await PlayAsync(stream, payload, 0, ct);
            var afterSeek = await PlayAsync(stream, payload, PayloadBytes / 4 * 3, ct);

            Report("From the start", fromStart);
            Report("After a seek three quarters in", afterSeek);

            Assert.Equal(PlayedBytes, fromStart.Played);
            Assert.Equal(PlayedBytes, afterSeek.Played);
        }
        finally
        {
            foreach (var seeder in seeders)
            {
                await seeder.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Plays <see cref="PlayedBytes"/> from <paramref name="from"/> as a player would: waits for a
    /// second's worth before it starts, then consumes at the bitrate. A read that is not back by the
    /// time the player needs it is a stall; like a player, it then buffers a second again before going on.
    /// </summary>
    private static async Task<Playback> PlayAsync(Stream stream, byte[] payload, long from, CancellationToken ct)
    {
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[ReadBytes];
        var clock = Stopwatch.StartNew();
        TimeSpan? firstByte = null;
        TimeSpan startedAt = TimeSpan.Zero;
        long playedFromStart = 0;
        long played = 0;
        int stalls = 0;
        var stalled = TimeSpan.Zero;
        bool playing = false;

        while (played < PlayedBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ReadBytes, PlayedBytes - played)), ct);
            Assert.True(read > 0);
            Assert.True(buffer.AsSpan(0, read).SequenceEqual(payload.AsSpan((int)(from + played), read)), "The stream returned the wrong bytes.");
            firstByte ??= clock.Elapsed;
            played += read;

            var now = clock.Elapsed;
            if (!playing)
            {
                if (played - playedFromStart >= StartupBufferBytes || played == PlayedBytes)
                {
                    playing = true;
                    startedAt = now;
                }

                continue;
            }

            // When playback, going since it last started, reaches what was just read. Late is a stall;
            // early by more than the buffer, the player waits rather than read further ahead.
            var needed = startedAt + TimeSpan.FromSeconds((double)(played - read - playedFromStart) / BitrateBytesPerSecond);
            var ahead = TimeSpan.FromSeconds((double)StartupBufferBytes / BitrateBytesPerSecond);
            if (now > needed + TimeSpan.FromMilliseconds(100))
            {
                stalls++;
                stalled += now - needed;
                playing = false;
                playedFromStart = played - read;
            }
            else if (now < needed - ahead)
            {
                await Task.Delay(needed - ahead - now, ct);
            }
        }

        return new Playback(firstByte ?? TimeSpan.Zero, played, stalls, stalled, clock.Elapsed);
    }

    private void Report(string what, Playback playback)
    {
        double seconds = (double)playback.Played / BitrateBytesPerSecond;
        string line =
            $"{what}: first byte after {playback.FirstByte.TotalSeconds:F2}s; {playback.Stalls} stalls, " +
            $"{playback.Stalled.TotalSeconds:F1}s stalled over {seconds:F0}s of film ({playback.Elapsed.TotalSeconds:F1}s in all)";

        // The test's output shows only where the runner reports it; a file named by
        // PEERSHARP_STREAMING_REPORT collects runs to compare.
        _output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }

    private async Task<ClientEngine> StartSeederAsync(TorrentFile torrentFile, byte[] payload, string name, int uploadBytesPerSecond)
    {
        string path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        await File.WriteAllBytesAsync(Path.Combine(path, "film.mkv"), payload, TestContext.Current.CancellationToken);

        var engine = await CreateEngineAsync(path);
        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        torrent.UploadLimitBytesPerSecond = uploadBytesPerSecond;
        await torrent.StartAsync();
        return engine;
    }

    private async Task<ClientEngine> CreateEngineAsync(string downloadPath, ILoggerFactory? loggerFactory = null)
    {
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = downloadPath },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                EnableUtpIn = false,
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Refuse,
            },
            Dht = { Enabled = false },
        };

        var engine = ClientEngine.Create(new TorrentClientOptions { LoggerFactory = loggerFactory ?? _loggerFactory, Settings = settings });
        await engine.InitializeAsync();
        return engine;
    }

    private static async Task ConnectAsync(ClientEngine leechEngine, ITorrent leech, List<ClientEngine> seeders, CancellationToken ct)
    {
        var endpoints = seeders
            .Select(seeder => new IPEndPoint(IPAddress.Loopback, (seeder.PortListener ?? throw new InvalidOperationException("A seeder has no port listener.")).Port))
            .ToList();

        var clock = Stopwatch.StartNew();
        while (leech.Peers.ConnectedCount < seeders.Count && clock.Elapsed < ConnectionTimeout)
        {
            leechEngine.OnPeersFound(leech.Hash, endpoints);
            await Task.Delay(200, ct);
        }

        Assert.Equal(seeders.Count, leech.Peers.ConnectedCount);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }

        _loggerFactory.Dispose();
    }

    private readonly record struct Playback(TimeSpan FirstByte, long Played, int Stalls, TimeSpan Stalled, TimeSpan Elapsed);
}
