using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Security.Cryptography;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Api;

/// <summary>
/// The controls a consumer needs that used to be reachable only from inside the assembly, or not at
/// all.
///
/// <para>
/// Super-seeding is the reason this file exists. BEP 16 was implemented, unit tested, and listed as
/// supported - and nothing anywhere ever set the flag that turns it on, because the manager holding it
/// is internal. A feature with no way in is indistinguishable from a missing one, so these tests come
/// at each control through <see cref="ITorrent"/> and <see cref="IClientEngine"/> only.
/// </para>
/// </summary>
[Collection("Integration")]
public class TorrentControlTests : IDisposable
{
    private const int PieceLength = 16 * 1024;

    private readonly ILoggerFactory _loggerFactory;
    private readonly string _testRoot;

    public TorrentControlTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpControl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task SuperSeeding_IsOffUntilAskedFor()
    {
        var (engine, torrent) = await CreateTorrentAsync("default-seed");
        await using var _ = engine;

        Assert.False(torrent.SuperSeeding);
    }

    [Fact]
    public async Task SuperSeeding_ReachesTheManagerThatImplementsIt()
    {
        // The gap this closes was exactly one assignment wide: the public switch has to arrive at the
        // internal flag the BEP 16 code reads, or the setting is decoration.
        var (engine, torrent) = await CreateTorrentAsync("super-seed");
        await using var _ = engine;

        torrent.SuperSeeding = true;

        Assert.True(torrent.SuperSeeding);
        Assert.True(((Torrent)torrent).SuperSeedManager.Enabled);

        torrent.SuperSeeding = false;

        Assert.False(((Torrent)torrent).SuperSeedManager.Enabled);
    }

    [Fact]
    public async Task SuperSeeding_CanBeAskedForWhenTheTorrentIsAdded()
    {
        var (engine, torrent) = await CreateTorrentAsync("super-seed-option", superSeeding: true);
        await using var _ = engine;

        Assert.True(torrent.SuperSeeding);
        Assert.True(((Torrent)torrent).SuperSeedManager.Enabled);
    }

    [Fact]
    public async Task SuperSeeding_IsHeldWhereAMetadataRebuildCannotLoseIt()
    {
        // White-box on purpose. The manager that owns the BEP 16 flag is thrown away and rebuilt when
        // a magnet's metadata arrives, so the caller's choice has to live somewhere that outlives it -
        // and the only way to state that as a test is to name the place.
        var (engine, torrent) = await CreateTorrentAsync("super-seed-durable");
        await using var _ = engine;

        torrent.SuperSeeding = true;

        Assert.True(((Torrent)torrent).Configuration.SuperSeeding);
    }

    [Fact]
    public async Task PiecePriority_OverridesWhatTheFileSelectionImplies()
    {
        var (engine, torrent) = await CreateTorrentAsync("piece-priority");
        await using var _ = engine;

        Assert.Equal(Priority.Normal, torrent.GetPiecePriority(0));

        torrent.SetPiecePriority(0, Priority.High);
        Assert.Equal(Priority.High, torrent.GetPiecePriority(0));

        // The neighbour must be unaffected, or the override is being applied to the whole torrent
        // rather than to one piece.
        Assert.Equal(Priority.Normal, torrent.GetPiecePriority(1));

        torrent.ClearPiecePriorities();
        Assert.Equal(Priority.Normal, torrent.GetPiecePriority(0));
    }

    [Fact]
    public async Task PiecePriority_ExcludesAPieceWhoseFileIsStillSelected()
    {
        // DoNotDownload on a piece has to reach the picker's "is this needed" question as well as its
        // "how badly" question. Answering only the second leaves the picker wanting a piece it then
        // ranks last, which looks like a stall rather than an exclusion.
        var (engine, torrent) = await CreateTorrentAsync("piece-excluded");
        await using var _ = engine;

        var internals = (Torrent)torrent;
        var context = new PiecePicking.TorrentPiecePickerContext(internals);
        var selection = internals.GetFileSelectionSnapshot();

        Assert.True(context.IsPieceNeeded(0, selection));

        torrent.SetPiecePriority(0, Priority.DoNotDownload);

        Assert.False(context.IsPieceNeeded(0, selection));
        Assert.True(context.IsPieceNeeded(1, selection));
    }

    [Fact]
    public async Task PiecePriority_RejectsAnIndexOutsideTheTorrent()
    {
        var (engine, torrent) = await CreateTorrentAsync("piece-range");
        await using var _ = engine;

        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.SetPiecePriority(-1, Priority.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.SetPiecePriority(torrent.PieceCount, Priority.High));
        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.GetPiecePriority(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.GetPiecePriority(torrent.PieceCount));
    }

    [Fact]
    public async Task ReadPiece_ReturnsWhatWasWritten()
    {
        var (engine, torrent, payload) = await CreateCompleteTorrentAsync("read-piece");
        await using var _ = engine;

        byte[] first = await torrent.ReadPieceAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(PieceLength, first.Length);
        Assert.Equal(payload.AsSpan(0, PieceLength).ToArray(), first);
    }

    [Fact]
    public async Task ReadPiece_ReturnsTheShorterLastPiece()
    {
        // The last piece is the one an implementation gets wrong, because every other piece is exactly
        // the piece length and a hardcoded size passes for all of them.
        var (engine, torrent, payload) = await CreateCompleteTorrentAsync("read-last-piece");
        await using var _ = engine;

        int last = torrent.PieceCount - 1;
        byte[] tail = await torrent.ReadPieceAsync(last, TestContext.Current.CancellationToken);

        int expectedLength = payload.Length - (last * PieceLength);

        Assert.Equal(expectedLength, tail.Length);
        Assert.Equal(payload.AsSpan(last * PieceLength).ToArray(), tail);
    }

    [Fact]
    public async Task ReadPiece_RefusesAPieceThatIsNotDownloaded()
    {
        var (engine, torrent) = await CreateTorrentAsync("read-missing");
        await using var _ = engine;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => torrent.ReadPieceAsync(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebSeeds_StartFromTheTorrentsOwnMetadata()
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-metadata", webSeed: "https://example.invalid/data/");
        await using var _ = engine;

        Assert.Equal(["https://example.invalid/data/"], torrent.WebSeeds.GetAll());
    }

    [Fact]
    public async Task WebSeeds_IgnoreUnusableUrlsInTorrentMetadata()
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-invalid-metadata", webSeed: "file:///tmp/data");
        await using var _ = engine;

        Assert.Empty(torrent.WebSeeds.GetAll());
    }

    [Fact]
    public async Task WebSeeds_CanBeAddedAndRemovedAtRuntime()
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-runtime", webSeed: "https://example.invalid/data/");
        await using var _ = engine;

        Assert.True(torrent.WebSeeds.Add("https://mirror.invalid/data/"));
        Assert.Equal(2, torrent.WebSeeds.GetAll().Count);

        // Adding the same URL twice must not double it up, or a caller re-applying its configuration
        // grows the list every time.
        Assert.False(torrent.WebSeeds.Add("https://mirror.invalid/data/"));
        Assert.Equal(2, torrent.WebSeeds.GetAll().Count);

        // Removing one the metadata declared has to work too, otherwise a publisher's dead mirror
        // cannot be got rid of.
        Assert.True(torrent.WebSeeds.Remove("https://example.invalid/data/"));
        Assert.Equal(["https://mirror.invalid/data/"], torrent.WebSeeds.GetAll());

        Assert.False(torrent.WebSeeds.Remove("https://never-added.invalid/"));
    }

    [Fact]
    public async Task WebSeeds_KeepMetadataUrlsThatAppearAfterAnAddition()
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-late-metadata");
        await using var _ = engine;

        Assert.True(torrent.WebSeeds.Add("https://mirror.invalid/data/"));

        // Metadata is replaced when a magnet finishes its BEP 9 exchange. The caller's overlay must
        // not freeze the empty pre-metadata list and thereby hide what the torrent later declares.
        ((Torrent)torrent).InfoFile.WebSeedUrls.Add("https://publisher.invalid/data/");

        Assert.Equal(
            ["https://publisher.invalid/data/", "https://mirror.invalid/data/"],
            torrent.WebSeeds.GetAll());
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("magnet:?xt=urn:btih:0000000000000000000000000000000000000000")]
    [InlineData("file:///etc/passwd")]
    public async Task WebSeeds_RefuseAnythingThatIsNotAWebSeedUrl(string url)
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-guard");
        await using var _ = engine;

        Assert.False(torrent.WebSeeds.Add(url));
        Assert.Empty(torrent.WebSeeds.GetAll());
    }

    [Fact]
    public async Task WebSeeds_RefuseBlankUrlsWithoutThrowing()
    {
        var (engine, torrent) = await CreateTorrentAsync("web-seed-blank");
        await using var _ = engine;

        Assert.False(torrent.WebSeeds.Add("   "));
        Assert.False(torrent.WebSeeds.Remove("   "));
    }

    [Fact]
    public async Task WebSeeds_AreRebuiltWhenATorrentRestarts()
    {
        var (engine, torrent) = await CreateTorrentAsync(
            "web-seed-restart",
            webSeed: "https://example.invalid/data/");
        await using var _ = engine;
        var internals = (Torrent)torrent;

        await torrent.StartAsync(TestContext.Current.CancellationToken);
        var first = Assert.IsType<Internals.Seeding.WebSeedManager>(internals.WebSeedManager);

        await torrent.StopAsync(TestContext.Current.CancellationToken);
        Assert.Null(internals.WebSeedManager);

        await torrent.StartAsync(TestContext.Current.CancellationToken);
        var second = Assert.IsType<Internals.Seeding.WebSeedManager>(internals.WebSeedManager);
        Assert.NotSame(first, second);
        Assert.Equal(["https://example.invalid/data/"], second.GetSourceUrls());
    }

    [Fact]
    public async Task WebSeeds_RemoveStopsARunningDirectorySeed()
    {
        const string url = "https://example.invalid/content/";
        var (engine, torrent) = await CreateTorrentAsync("web-seed-live-remove", webSeed: url);
        await using var _ = engine;
        await torrent.StartAsync(TestContext.Current.CancellationToken);
        var manager = Assert.IsType<Internals.Seeding.WebSeedManager>(((Torrent)torrent).WebSeedManager);

        Assert.True(torrent.WebSeeds.Remove(url));
        Assert.Empty(manager.GetSourceUrls());
    }

    [Fact]
    public async Task PerTorrentLimits_DefaultToTheEngineWideSettings()
    {
        var (engine, torrent) = await CreateTorrentAsync("limits-default");
        await using var _ = engine;

        Assert.Equal(0, torrent.MaxConnections);
        Assert.Equal(0, torrent.MaxUploadSlots);
    }

    [Fact]
    public async Task PerTorrentLimits_RejectNegativeValues()
    {
        var (engine, torrent) = await CreateTorrentAsync("limits-guard");
        await using var _ = engine;

        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.MaxConnections = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => torrent.MaxUploadSlots = -1);
    }

    [Fact]
    public async Task MaxUploadSlots_IsUsedAsGivenRatherThanCalculated()
    {
        var (engine, torrent) = await CreateTorrentAsync("upload-slots");
        await using var _ = engine;

        var choker = new Internals.Peers.PeerChoker(
            (Torrent)torrent,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // Without an explicit number the count comes from the upload limit and the min/max settings.
        // With one, it is that number - bounded only by how many peers are actually connected.
        torrent.MaxUploadSlots = 3;

        Assert.Equal(3, choker.GetUploadSlotsForTesting(connectedCount: 20));
        Assert.Equal(2, choker.GetUploadSlotsForTesting(connectedCount: 2));
    }

    [Fact]
    public async Task SessionPause_StopsWhatWasRunningAndResumeStartsItAgain()
    {
        await using var engine = await CreateEngineAsync();

        var running = await AddTorrentAsync(engine, "paused-running", start: true);
        var alreadyStopped = await AddTorrentAsync(engine, "paused-stopped", start: false);

        Assert.True(running.Started);
        Assert.False(alreadyStopped.Started);

        await engine.PauseAsync(TestContext.Current.CancellationToken);

        Assert.True(engine.IsPaused);
        Assert.False(running.Started);

        await engine.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.False(engine.IsPaused);
        Assert.True(running.Started);

        // The torrent the user had stopped by hand must stay stopped. Resuming everything the engine
        // holds would silently override a deliberate choice.
        Assert.False(alreadyStopped.Started);
    }

    [Fact]
    public async Task SessionPause_HoldsBackATorrentAddedWhilePaused()
    {
        await using var engine = await CreateEngineAsync();

        await engine.PauseAsync(TestContext.Current.CancellationToken);

        var added = await AddTorrentAsync(engine, "added-while-paused", start: true);

        Assert.False(added.Started);

        await engine.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.True(added.Started);
    }

    [Fact]
    public async Task SessionPause_HoldsBackAManualStartUntilResume()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddTorrentAsync(engine, "manually-started-while-paused", start: false);

        await engine.PauseAsync(TestContext.Current.CancellationToken);
        await torrent.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(engine.IsPaused);
        Assert.False(torrent.Started);

        await engine.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.True(torrent.Started);
    }

    [Fact]
    public async Task SessionPause_IgnoresASecondCall()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddTorrentAsync(engine, "double-pause", start: true);

        await engine.PauseAsync(TestContext.Current.CancellationToken);
        await engine.PauseAsync(TestContext.Current.CancellationToken);
        await engine.ResumeAsync(TestContext.Current.CancellationToken);

        // A second pause must not record the torrent as "was not running" and lose it on resume.
        Assert.True(torrent.Started);
    }

    [Fact]
    public async Task SessionResume_SurvivesATorrentRemovedWhilePaused()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddTorrentAsync(engine, "removed-while-paused", start: true);

        await engine.PauseAsync(TestContext.Current.CancellationToken);
        await engine.RemoveTorrentAsync(torrent, cancellationToken: TestContext.Current.CancellationToken);

        await engine.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.False(engine.IsPaused);
        Assert.Empty(engine.GetTorrents());
    }

    [Fact]
    public async Task SessionPause_PreCancelledCallDoesNotChangeSessionState()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddTorrentAsync(engine, "cancelled-pause", start: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PauseAsync(cancellation.Token));

        Assert.False(engine.IsPaused);
        Assert.True(torrent.Started);
    }

    [Fact]
    public async Task SessionResume_PreCancelledCallCanBeRetried()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddTorrentAsync(engine, "cancelled-resume", start: true);
        await engine.PauseAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ResumeAsync(cancellation.Token));

        Assert.True(engine.IsPaused);
        Assert.False(torrent.Started);

        await engine.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.False(engine.IsPaused);
        Assert.True(torrent.Started);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();

        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle the OS has not released yet. The temp directory is not this test's subject.
        }
    }

    private async Task<ClientEngine> CreateEngineAsync()
    {
        string downloadPath = Path.Combine(_testRoot, "engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(downloadPath);

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = downloadPath },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false }
            }
        });

        await engine.InitializeAsync(TestContext.Current.CancellationToken);
        return engine;
    }

    private static async Task<ITorrent> AddTorrentAsync(ClientEngine engine, string name, bool start)
    {
        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(name)
            .WithPieceLength(PieceLength)
            .AddFile(name + ".bin", RandomNumberGenerator.GetBytes(PieceLength * 2))
            .Build();

        return await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions { StartImmediately = start },
            TestContext.Current.CancellationToken);
    }

    private async Task<(ClientEngine Engine, ITorrent Torrent)> CreateTorrentAsync(
        string name,
        bool superSeeding = false,
        string? webSeed = null)
    {
        var engine = await CreateEngineAsync();

        var builder = new ApiTorrentFileBuilder()
            .WithName(name)
            .WithPieceLength(PieceLength)
            .AddFile(name + ".bin", RandomNumberGenerator.GetBytes(PieceLength * 3));

        if (webSeed != null)
        {
            builder.AddWebSeed(webSeed);
        }

        var torrent = await engine.AddTorrentAsync(
            builder.Build(),
            new AddTorrentOptions { StartImmediately = false, SuperSeeding = superSeeding },
            TestContext.Current.CancellationToken);

        return (engine, torrent);
    }

    /// <summary>
    /// A torrent whose single file is already on disk and verified, for the read-back tests.
    /// </summary>
    private async Task<(ClientEngine Engine, ITorrent Torrent, byte[] Payload)> CreateCompleteTorrentAsync(string name)
    {
        string downloadPath = Path.Combine(_testRoot, name + "-root");
        Directory.CreateDirectory(downloadPath);

        // Deliberately not a whole number of pieces, so the last one is short.
        byte[] payload = RandomNumberGenerator.GetBytes((PieceLength * 2) + 511);

        // A single-file torrent stores its content under the torrent's own name, not under the name
        // given to AddFile, so the two have to agree for the recheck to find anything.
        string fileName = name + ".bin";
        await File.WriteAllBytesAsync(
            Path.Combine(downloadPath, fileName),
            payload,
            TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(PieceLength)
            .AddFile(fileName, payload)
            .Build();

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = downloadPath },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false }
            }
        });

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        var torrent = await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions(downloadPath) { StartImmediately = false },
            TestContext.Current.CancellationToken);

        int verified = await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(torrent.PieceCount, verified);

        return (engine, torrent, payload);
    }
}
