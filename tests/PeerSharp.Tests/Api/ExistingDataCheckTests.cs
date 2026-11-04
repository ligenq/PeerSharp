using PeerSharp.Internals;
using System.Security.Cryptography;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Api;

/// <summary>
/// What the rest of the engine can do while a torrent hashes its files.
///
/// <para>
/// A first start over existing data runs for as long as the disk takes, with the torrent's state lock
/// held. The disk read limit keeps these checks far longer than any test, so each one that finishes
/// did so because the check was ended or got out of the way, not because it happened to be over.
/// </para>
/// </summary>
public sealed class ExistingDataCheckTests : IDisposable
{
    private const int PieceLength = 16 * 1024;
    private const int PayloadSize = 8 * 1024 * 1024;
    private const long SlowRead = 64 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "peersharp-check-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30000)]
    public async Task Stop_EndsACheck_AndTheStartCompletesWithTheTorrentStopped()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddOverExistingFileAsync(engine, "stop", startImmediately: false);
        var start = torrent.StartAsync(Token);
        await UntilCheckingAsync(torrent);

        await torrent.StopAsync(Token);
        await start;

        Assert.False(torrent.Started);
        Assert.Equal(TorrentState.Stopped, torrent.State);

        // Nothing was kept from the check that was ended, so the next start has to do it again.
        _ = torrent.StartAsync(Token);
        await UntilCheckingAsync(torrent);
    }

    [Fact(Timeout = 30000)]
    public async Task CancellingTheStart_EndsACheck_AndFailsTheStart()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddOverExistingFileAsync(engine, "cancel", startImmediately: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var start = torrent.StartAsync(cancellation.Token);
        await UntilCheckingAsync(torrent);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(torrent.Started);
    }

    /// <summary>A stop is not a failure, so the add must not throw the torrent away for it.</summary>
    [Fact(Timeout = 30000)]
    public async Task StoppingATorrentDuringItsAdd_KeepsTheTorrent()
    {
        await using var engine = await CreateEngineAsync();
        var add = AddOverExistingFileAsync(engine, "add-stop", startImmediately: true);
        var torrent = await UntilOneIsCheckingAsync(engine);

        await torrent.StopAsync(Token);

        Assert.Same(torrent, await add);
        Assert.Contains(torrent, engine.GetTorrents());
        Assert.False(torrent.Started);
    }

    /// <summary>
    /// A removal holds the engine's lifecycle lock while it stops the torrent, so waiting for the
    /// check here would hold up every other removal and shutdown. And the add is still running when
    /// the removal deletes the session entry, so it must not write the torrent back afterwards.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task RemovingATorrentDuringItsAdd_EndsTheCheck_AndLeavesNothingInTheSession()
    {
        string session = Path.Combine(_root, "session");
        await using (var engine = await CreateEngineAsync(session))
        {
            var add = AddOverExistingFileAsync(engine, "remove", startImmediately: true);
            var torrent = await UntilOneIsCheckingAsync(engine);

            await engine.RemoveTorrentAsync(torrent, cancellationToken: Token);
            await add;

            Assert.Empty(engine.GetTorrents());
        }

        await using var restored = await CreateEngineAsync(session);
        Assert.Empty(restored.GetTorrents());
    }

    [Fact(Timeout = 30000)]
    public async Task DisposingTheEngine_EndsACheck()
    {
        var engine = await CreateEngineAsync();
        var torrent = await AddOverExistingFileAsync(engine, "dispose", startImmediately: false);
        var start = torrent.StartAsync(Token);
        await UntilCheckingAsync(torrent);

        await engine.DisposeAsync();

        await start;
    }

    [Fact(Timeout = 30000)]
    public async Task ForceRecheck_EndedByAStop_ThrowsAndLeavesNothingBehind()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddOverExistingFileAsync(engine, "recheck", startImmediately: false);
        var recheck = torrent.ForceRecheckAsync(cancellationToken: Token);
        await UntilCheckingAsync(torrent);

        await torrent.StopAsync(Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recheck);
        Assert.Equal(TorrentState.Stopped, torrent.State);
        Assert.Equal(0, torrent.PiecesReceived);
    }

    /// <summary>
    /// The start kept the engine's pause gate through the check, so every other start waited behind
    /// it, including the one inside any add.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACheck_DoesNotHoldUpOtherTorrents()
    {
        await using var engine = await CreateEngineAsync();
        var checking = await AddOverExistingFileAsync(engine, "busy", startImmediately: false);
        _ = checking.StartAsync(Token);
        await UntilCheckingAsync(checking);

        var other = await engine.AddTorrentAsync(
            NewMetadata("other.bin"), new AddTorrentOptions(DownloadPath("other")), Token);

        Assert.True(other.Started);
        Assert.Equal(TorrentState.CheckingFiles, checking.State);
    }

    /// <summary>
    /// A torrent that is checking is not Started, and without this a pause would pass over it and
    /// the torrent would start when its check finished, in a session that had been paused.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pause_EndsACheck_AndResumeStartsTheTorrentAgain()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await AddOverExistingFileAsync(engine, "pause", startImmediately: false);
        var start = torrent.StartAsync(Token);
        await UntilCheckingAsync(torrent);

        await engine.PauseAsync(Token);
        await start;

        Assert.True(engine.IsPaused);
        Assert.False(torrent.Started);

        _ = engine.ResumeAsync(Token);
        await UntilCheckingAsync(torrent);
        Assert.False(engine.IsPaused);
    }

    /// <summary>
    /// A magnet is rebuilt and restarted when its metadata arrives, which is when it finds its files
    /// and checks them. That restart happens inside the torrent, with the torrent not started, and a
    /// pause has to find it all the same or it starts in a paused session once the check ends.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pause_FindsAMagnetCheckingItsFilesAfterItsMetadataArrived()
    {
        const int size = 128 * 1024;
        await using var engine = await CreateEngineAsync();
        string downloadPath = DownloadPath("magnet");
        byte[] payload = RandomNumberGenerator.GetBytes(size);
        await File.WriteAllBytesAsync(Path.Combine(downloadPath, "magnet.bin"), payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("magnet.bin").WithPieceLength(size).AddFile("magnet.bin", payload).Build();
        var torrent = (Torrent)await engine.AddMagnetAsync(
            $"magnet:?xt=urn:btih:{metadata.InfoHash.ToHexString()}", new AddTorrentOptions(downloadPath), Token);
        Assert.True(torrent.Started);

        var info = torrent.InfoFile.Info;
        info.Name = "magnet.bin";
        info.PieceSize = size;
        info.FullSize = size;
        info.Files.Add(new Internals.TorrentFileEntry { Path = "magnet.bin", Size = size, Offset = 0 });
#pragma warning disable CA5350 // BEP 3 requires SHA-1 piece hashes.
        info.Pieces.Add(SHA1.HashData(payload));
#pragma warning restore CA5350
        torrent.InfoFile.InfoBytes = RandomNumberGenerator.GetBytes(32);
        var rebuild = torrent.ReinitializeAfterMetadataAsync(Token);
        await UntilCheckingAsync(torrent);

        await engine.PauseAsync(Token);
        await rebuild;

        Assert.True(engine.IsPaused);
        Assert.False(torrent.Started);

        await engine.ResumeAsync(Token);
        Assert.True(torrent.Started);
    }

    /// <summary>
    /// Resuming is how a restored session starts. One torrent with data on disk and no resume data
    /// must not leave the session paused, and everything else in it stopped, until it is hashed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Resume_ResumesTheSessionBeforeAnyTorrentChecksItsFiles()
    {
        await using var engine = await CreateEngineAsync();
        await engine.PauseAsync(Token);
        var checking = await AddOverExistingFileAsync(engine, "resume", startImmediately: true);
        var other = await engine.AddTorrentAsync(
            NewMetadata("resume-other.bin"), new AddTorrentOptions(DownloadPath("resume-other")), Token);
        Assert.False(other.Started);

        var resume = engine.ResumeAsync(Token);
        await UntilCheckingAsync(checking);

        Assert.False(engine.IsPaused);
        await UntilAsync(() => other.Started);
        Assert.False(resume.IsCompleted);

        // Still paused-then-resumable while that resume is finishing its checks.
        await engine.PauseAsync(Token);
        await resume;
        Assert.True(engine.IsPaused);
        Assert.False(checking.Started);
        Assert.False(other.Started);
    }

    /// <summary>The order a pause meets a start that has been let through but has not got anywhere.</summary>
    [Fact(Timeout = 30000)]
    public async Task AStopThatOvertakesAStart_Wins()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = (Torrent)await engine.AddTorrentAsync(
            NewMetadata("overtaken.bin"),
            new AddTorrentOptions(DownloadPath("overtaken")) { StartImmediately = false },
            Token);

        long stopRequests = torrent.BeginStart();
        await torrent.StopAsync(Token);
        await torrent.StartCoreAsync(stopRequests, Token);
        torrent.EndStart();

        Assert.False(torrent.Started);

        // Only one that came after: a stop before the start has no say in it.
        await torrent.StartAsync(Token);
        Assert.True(torrent.Started);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A handle the OS has not released yet. The temp directory is not these tests' subject.
        }
    }

    private async Task<ClientEngine> CreateEngineAsync(string? sessionPath = null)
    {
        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = _root, MaxDiskReadSpeed = SlowRead },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false },
                Session = { Enabled = sessionPath != null, SessionPath = sessionPath ?? string.Empty }
            }
        });
        await engine.InitializeAsync(Token);
        return engine;
    }

    private string DownloadPath(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static TorrentFile NewMetadata(string fileName) => new ApiTorrentFileBuilder()
        .WithName(fileName)
        .WithPieceLength(PieceLength)
        .AddFile(fileName, RandomNumberGenerator.GetBytes(PieceLength))
        .Build();

    private async Task<ITorrent> AddOverExistingFileAsync(ClientEngine engine, string name, bool startImmediately)
    {
        string downloadPath = DownloadPath(name);
        string fileName = name + ".bin";
        byte[] payload = RandomNumberGenerator.GetBytes(PayloadSize);
        await File.WriteAllBytesAsync(Path.Combine(downloadPath, fileName), payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(PieceLength)
            .AddFile(fileName, payload)
            .Build();

        return await engine.AddTorrentAsync(
            metadata, new AddTorrentOptions(downloadPath) { StartImmediately = startImmediately }, Token);
    }

    private static Task UntilCheckingAsync(ITorrent torrent) =>
        UntilAsync(() => torrent.State == TorrentState.CheckingFiles);

    private static async Task<ITorrent> UntilOneIsCheckingAsync(ClientEngine engine)
    {
        ITorrent? checking = null;
        await UntilAsync(() =>
        {
            checking = engine.GetTorrents().FirstOrDefault(t => t.State == TorrentState.CheckingFiles);
            return checking != null;
        });
        return checking!;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(5, Token);
        }
    }
}
