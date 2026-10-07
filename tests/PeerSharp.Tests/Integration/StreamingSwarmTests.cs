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

        await InSwarmAsync(async (stream, payload, ct) =>
        {
            var fromStart = await PlayAsync(stream, payload, 0, ct);
            var afterSeek = await PlayAsync(stream, payload, PayloadBytes / 4 * 3, ct);

            Report("From the start", fromStart);
            Report("After a seek three quarters in", afterSeek);

            Assert.Equal(PlayedBytes, fromStart.Played);
            Assert.Equal(PlayedBytes, afterSeek.Played);
        });
    }

    /// <summary>
    /// Opening a film part-way in - carrying on where it was left - with nothing of it downloaded:
    /// what the player waits for is the piece where it resumes, not the start of the file.
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task APlayer_ResumingPartWayIn_IsKeptFed()
    {
        Assert.SkipUnless(Enabled, "Set PEERSHARP_STREAMING=1 to measure streaming against a local swarm.");

        await InSwarmAsync(async (stream, payload, ct) =>
        {
            var resumed = await PlayAsync(stream, payload, PayloadBytes / 3, ct);

            Report("Resuming a third in", resumed);

            Assert.Equal(PlayedBytes, resumed.Played);
        });
    }

    /// <summary>
    /// A 32 Mbit/s film - a 4K Blu-ray's rate - with the fast seeder at twice that, until ten seconds
    /// in it slows to a crawl for six. What carries the player through is what was fetched ahead
    /// before: a window of so many bytes is a few seconds of a film like this, one of so many seconds is
    /// as long at any rate.
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task APlayer_RidesOutTheFastSeederSlowingDown()
    {
        Assert.SkipUnless(Enabled, "Set PEERSHARP_STREAMING=1 to measure streaming against a local swarm.");
        const int Bitrate = 4 * 1024 * 1024;

        await InSwarmAsync(
            async (stream, payload, fastSeeder, ct) =>
            {
                var slowing = SlowDownAsync(fastSeeder, after: TimeSpan.FromSeconds(10), lasting: TimeSpan.FromSeconds(6), restoreTo: 2 * Bitrate, ct);
                var played = await PlayAsync(stream, payload, 0, ct, Bitrate, length: 24 * Bitrate);
                await slowing;

                Report("A 32 Mbit/s film, the fast seeder slowing for 6s", played);

                Assert.Equal(24 * Bitrate, played.Played);
            },
            fastSeederBytesPerSecond: 2 * Bitrate);
    }

    /// <summary>
    /// A 4K web release as Cast receivers play it: 4 MiB pieces, a film of about 0.8 MB/s, and a swarm
    /// of mixed peers - a few fast, more middling, two crawling - that between them deliver several times
    /// the film's rate. A stream hands out only whole, verified pieces, so what a receiver feels is the
    /// longest a single read waits: one that waits more than <see cref="ReceiverReadTimeout"/> is one a
    /// receiver gives up on, dropping its connection and reloading the film where it was.
    /// </summary>
    [Fact(Timeout = 600000)]
    public async Task APlayer_OfABigPieceFilm_IsNotKeptWaitingLongEnoughToGiveUp()
    {
        Assert.SkipUnless(Enabled, "Set PEERSHARP_STREAMING=1 to measure streaming against a local swarm.");
        const int Bitrate = 800 * 1024;
        const int Seconds = 150;

        await InSwarmAsync(
            async (stream, payload, _, ct) =>
            {
                var played = await PlayAsync(stream, payload, 0, ct, Bitrate, length: (long)Seconds * Bitrate, Player.ExoPlayer);
                Report("A 4K film in 4 MiB pieces, a mixed swarm", played);
                Assert.Equal((long)Seconds * Bitrate, played.Played);
            },
            pieceLength: 4 * 1024 * 1024,
            payloadBytes: 160 * 1024 * 1024,
            seederRates: SwarmFromEnvironment() ?? [3 * 1024 * 1024, 2 * 1024 * 1024, 1024 * 1024, 512 * 1024, 256 * 1024, 96 * 1024, 96 * 1024]);
    }

    /// <summary>
    /// PEERSHARP_STREAMING_SWARM, as <c>24x384</c> - two dozen seeders at 384 KiB/s - or a list of such
    /// groups separated by commas; null when unset.
    /// </summary>
    private static List<int>? SwarmFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_SWARM") is not { Length: > 0 } swarm)
        {
            return null;
        }

        var rates = new List<int>();
        foreach (string group in swarm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = group.Split('x');
            rates.AddRange(Enumerable.Repeat(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)));
        }

        return rates;
    }

    private static async Task SlowDownAsync(ITorrent seeder, TimeSpan after, TimeSpan lasting, int restoreTo, CancellationToken ct)
    {
        await Task.Delay(after, ct);
        seeder.UploadLimitBytesPerSecond = 64 * 1024;
        await Task.Delay(lasting, ct);
        seeder.UploadLimitBytesPerSecond = restoreTo;
    }

    /// <summary>
    /// Runs <paramref name="play"/> on a stream of the film from a swarm of one fast seeder and
    /// <see cref="SlowSeeders"/> slow ones, as soon as the player's engine is connected to them all.
    /// </summary>
    private async Task InSwarmAsync(Func<Stream, byte[], CancellationToken, Task> play, int fastSeederBytesPerSecond = FastSeederBytesPerSecond) =>
        await InSwarmAsync((stream, payload, _, ct) => play(stream, payload, ct), fastSeederBytesPerSecond);

    /// <param name="seederRates">
    /// Upload rates of the seeders, the first of them handed to <paramref name="play"/>; when null, one
    /// fast seeder at <paramref name="fastSeederBytesPerSecond"/> and <see cref="SlowSeeders"/> slow ones.
    /// </param>
    private async Task InSwarmAsync(
        Func<Stream, byte[], ITorrent, CancellationToken, Task> play,
        int fastSeederBytesPerSecond = FastSeederBytesPerSecond,
        int pieceLength = PieceLength,
        int payloadBytes = PayloadBytes,
        IReadOnlyList<int>? seederRates = null)
    {
        var ct = TestContext.Current.CancellationToken;

        byte[] payload = new byte[payloadBytes];
        new Random(7).NextBytes(payload);
        var torrentFile = new ApiTorrentFileBuilder()
            .WithName("film.mkv")
            .WithPieceLength((uint)pieceLength)
            .AddFile("film.mkv", payload)
            .Build();

        var rates = seederRates ?? [fastSeederBytesPerSecond, .. Enumerable.Repeat(SlowSeederBytesPerSecond, SlowSeeders)];
        var seeders = new List<ClientEngine>();
        try
        {
            ITorrent? fastSeeder = null;
            for (int i = 0; i < rates.Count; i++)
            {
                var (engine, seeder) = await StartSeederAsync(torrentFile, payload, $"seeder{i}", rates[i]);
                seeders.Add(engine);
                fastSeeder ??= seeder;
            }

            // PEERSHARP_STREAMING_LOG names a file the player's engine logs to, in detail, for a run to look into.
            using var leechLog = Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_LOG") is { Length: > 0 } logPath
                ? LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new PeerSharp.Tests.Interop.TimestampedFileLoggerProvider(logPath)))
                : null;
            await using var leechEngine = await CreateEngineAsync(Path.Combine(_testRoot, "leech"), leechLog);
            var leech = await leechEngine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = true });
            await ConnectAsync(leechEngine, leech, seeders, ct);

            await using var stream = await leech.OpenStreamAsync(0, ct);
            await play(stream, payload, fastSeeder!, ct);
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
    /// <param name="onStall">Told of each stall: where in the film it came, how long it lasted, and the offset read late.</param>
    internal static async Task<Playback> PlayAsync(
        Stream stream, byte[]? payload, long from, CancellationToken ct, int bitrate = BitrateBytesPerSecond, long length = PlayedBytes, Player? player = null,
        Action<TimeSpan, TimeSpan, long>? onStall = null)
    {
        var buffering = player ?? Player.Strict;
        long startupBuffer = (long)(buffering.Start.TotalSeconds * bitrate);
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[ReadBytes];
        var clock = Stopwatch.StartNew();
        TimeSpan? firstByte = null;
        TimeSpan? firstStarted = null;
        TimeSpan startedAt = TimeSpan.Zero;
        long playedFromStart = 0;
        long played = 0;
        int stalls = 0;
        var stalled = TimeSpan.Zero;
        bool playing = false;
        var longestRead = TimeSpan.Zero;
        int longReads = 0;

        while (played < length)
        {
            var readStarted = clock.Elapsed;
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ReadBytes, length - played)), ct);
            var readTook = clock.Elapsed - readStarted;
            longestRead = readTook > longestRead ? readTook : longestRead;
            longReads += readTook > ReceiverReadTimeout ? 1 : 0;
            Assert.True(read > 0);
            Assert.True(payload is null || buffer.AsSpan(0, read).SequenceEqual(payload.AsSpan((int)(from + played), read)), "The stream returned the wrong bytes.");
            firstByte ??= clock.Elapsed;
            played += read;

            var now = clock.Elapsed;
            if (!playing)
            {
                if (played - playedFromStart >= startupBuffer || played == length)
                {
                    playing = true;
                    startedAt = now;
                    firstStarted ??= now;
                }

                continue;
            }

            // When playback, going since it last started, reaches what was just read. Late is a stall;
            // early by more than the buffer, the player waits rather than read further ahead.
            var needed = startedAt + TimeSpan.FromSeconds((double)(played - read - playedFromStart) / bitrate);
            if (now > needed + TimeSpan.FromMilliseconds(100))
            {
                stalls++;
                stalled += now - needed;
                onStall?.Invoke(TimeSpan.FromSeconds((double)(played - read) / bitrate), now - needed, from + played - read);
                playing = false;
                playedFromStart = played - read;
                startupBuffer = (long)(buffering.AfterStall.TotalSeconds * bitrate);
            }
            else if (now < needed - buffering.Ahead)
            {
                await Task.Delay(needed - buffering.Ahead - now, ct);
            }
        }

        return new Playback(firstByte ?? TimeSpan.Zero, played, stalls, stalled, clock.Elapsed, bitrate, firstStarted ?? clock.Elapsed, longestRead, longReads);
    }

    private void Report(string what, Playback playback)
    {
        double seconds = (double)playback.Played / playback.Bitrate;
        string line =
            $"{what}: first byte after {playback.FirstByte.TotalSeconds:F2}s; {playback.Stalls} stalls, " +
            $"{playback.Stalled.TotalSeconds:F1}s stalled over {seconds:F0}s of film ({playback.Elapsed.TotalSeconds:F1}s in all); " +
            $"longest read {playback.LongestRead.TotalSeconds:F1}s, {playback.LongReads} longer than {ReceiverReadTimeout.TotalSeconds:F0}s";

        // The test's output shows only where the runner reports it; a file named by
        // PEERSHARP_STREAMING_REPORT collects runs to compare.
        _output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("PEERSHARP_STREAMING_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }
    }

    private async Task<(ClientEngine Engine, ITorrent Torrent)> StartSeederAsync(TorrentFile torrentFile, byte[] payload, string name, int uploadBytesPerSecond)
    {
        string path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        await File.WriteAllBytesAsync(Path.Combine(path, "film.mkv"), payload, TestContext.Current.CancellationToken);

        var engine = await CreateEngineAsync(path);
        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        torrent.UploadLimitBytesPerSecond = uploadBytesPerSecond;
        await torrent.StartAsync();
        return (engine, torrent);
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

    internal readonly record struct Playback(
        TimeSpan FirstByte, long Played, int Stalls, TimeSpan Stalled, TimeSpan Elapsed, int Bitrate, TimeSpan StartedPlaying,
        TimeSpan LongestRead = default, int LongReads = 0);

    /// <summary>
    /// How long a Cast receiver lets one read of the stream go unanswered before it drops the connection
    /// and reloads the film where it was - which a viewer sees as a stall of a second or two. Measured in
    /// Peercine: a receiver reloaded about once a minute while 4 MiB pieces kept its reads waiting.
    /// </summary>
    internal static readonly TimeSpan ReceiverReadTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How a player buffers: what it waits for before it starts, and again after a stall, and how far ahead it reads.</summary>
    internal sealed record Player(string Name, TimeSpan Start, TimeSpan AfterStall, TimeSpan Ahead)
    {
        /// <summary>A second of buffer, always: every late read shows.</summary>
        public static Player Strict { get; } = new("1s buffer", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        /// <summary>
        /// ExoPlayer's defaults (Media3's DefaultLoadControl), as Android and Android TV players use them:
        /// playing once 2.5 seconds are buffered, 5 after a stall, and reading up to 50 seconds ahead.
        /// </summary>
        public static Player ExoPlayer { get; } = new("ExoPlayer", TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(50));
    }
}
