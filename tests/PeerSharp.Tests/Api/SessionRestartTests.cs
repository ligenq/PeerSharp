using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using System.Security.Cryptography;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Api;

/// <summary>
/// What a session comes back as after it is closed and opened again. A restart should leave each
/// torrent where the person left it, which is not always what it was doing at that moment: a torrent
/// waiting for a queue slot, held by a session pause or still checking its files is not running, and
/// is not stopped either.
/// </summary>
public sealed class SessionRestartTests : IDisposable
{
    private const int PieceLength = 16 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "peersharp-restart-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30000)]
    public async Task ATorrentWaitingForAQueueSlot_StaysInTheQueue()
    {
        string waitingName;
        string runningName;
        await using (var engine = await CreateEngineAsync(queue: true))
        {
            var first = await engine.AddTorrentAsync(NewMetadata("first.bin"), new AddTorrentOptions(_root), Token);
            var second = await engine.AddTorrentAsync(NewMetadata("second.bin"), new AddTorrentOptions(_root), Token);
            await UntilAsync(() => first.Started != second.Started);
            var waiting = first.Started ? second : first;
            Assert.True(waiting.QueueAutoStart);
            waitingName = waiting.Name;
            runningName = (first.Started ? first : second).Name;
        }

        await using var restored = await CreateEngineAsync(queue: true);
        var back = Single(restored, waitingName);
        Assert.True(back.QueueAutoStart);

        // Room opens up, and the queue takes it as it would have without the restart.
        var other = Single(restored, runningName);
        other.QueueAutoStart = false;
        await other.StopAsync(Token);
        await UntilAsync(() => back.Started);
    }

    [Fact(Timeout = 30000)]
    public async Task ATorrentStoppedAndTakenOutOfTheQueue_StaysStopped()
    {
        await using (var engine = await CreateEngineAsync(queue: true))
        {
            var torrent = await engine.AddTorrentAsync(NewMetadata("stopped.bin"), new AddTorrentOptions(_root), Token);
            torrent.QueueAutoStart = false;
            await torrent.StopAsync(Token);
        }

        await using var restored = await CreateEngineAsync(queue: true);
        var back = Assert.Single(restored.GetTorrents());

        // Long enough for the queue to have taken it, were it going to.
        await Task.Delay(TimeSpan.FromSeconds(2.5), Token);

        Assert.False(back.Started);
        Assert.False(back.QueueAutoStart);
    }

    [Fact(Timeout = 30000)]
    public async Task APausedSession_ComesBackPaused_AndResumeStartsWhatThePauseStopped()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(NewMetadata("paused.bin"), new AddTorrentOptions(_root), Token);
            await engine.AddTorrentAsync(NewMetadata("idle.bin"), new AddTorrentOptions(_root) { StartImmediately = false }, Token);
            await engine.PauseAsync(Token);
        }

        await using var restored = await CreateEngineAsync();
        var paused = Single(restored, "paused.bin");
        var idle = Single(restored, "idle.bin");
        Assert.True(restored.IsPaused);
        Assert.False(paused.Started);

        await restored.ResumeAsync(Token);

        Assert.True(paused.Started);
        Assert.False(idle.Started);
    }

    /// <summary>
    /// An application may pause the engine itself before initializing it, to have things in place
    /// before restored torrents reach peers. Whether to resume afterwards is then only its decision if
    /// it can tell its pause from the one the person left.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnEnginePausedBeforeInitializing_SaysWhetherTheSessionWasPaused()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(NewMetadata("paused.bin"), new AddTorrentOptions(_root), Token);
        }

        await using (var first = NewEngine())
        {
            await first.PauseAsync(Token);
            await first.InitializeAsync(Token);
            Assert.False(first.SessionRestoredPaused);
            await first.ResumeAsync(Token);
            await first.PauseAsync(Token);
        }

        await using var second = NewEngine();
        await second.PauseAsync(Token);
        await second.InitializeAsync(Token);
        Assert.True(second.SessionRestoredPaused);
    }

    /// <summary>With nothing in it there is no torrent to carry the pause.</summary>
    [Fact(Timeout = 30000)]
    public async Task AnEmptySessionPaused_ComesBackPaused()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.PauseAsync(Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.True(restored.IsPaused);
        Assert.True(restored.SessionRestoredPaused);
    }

    [Fact(Timeout = 30000)]
    public async Task RemovingTheLastTorrentWhilePaused_LeavesTheSessionPaused()
    {
        await using (var engine = await CreateEngineAsync())
        {
            var torrent = await engine.AddTorrentAsync(NewMetadata("removed.bin"), new AddTorrentOptions(_root), Token);
            await engine.PauseAsync(Token);
            await engine.RemoveTorrentAsync(torrent, cancellationToken: Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.Empty(restored.GetTorrents());
        Assert.True(restored.IsPaused);
    }

    [Fact(Timeout = 30000)]
    public async Task ASessionLeftPaused_StaysPaused_ThroughARestartThatDoesNotTouchIt()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.PauseAsync(Token);
        }

        await using (await CreateEngineAsync())
        {
        }

        await using var restored = await CreateEngineAsync();

        Assert.True(restored.IsPaused);
    }

    /// <summary>A crash saves nothing, so what a restart comes back as cannot wait for the next save.</summary>
    [Fact(Timeout = 30000)]
    public async Task APauseAndAResume_AreSavedAsTheyHappen()
    {
        await using var engine = await CreateEngineAsync();
        await engine.AddTorrentAsync(NewMetadata("saved.bin"), new AddTorrentOptions(_root), Token);

        await engine.PauseAsync(Token);
        Assert.True((await LoadSessionStateAsync())?.Paused);

        await engine.ResumeAsync(Token);
        Assert.False((await LoadSessionStateAsync())?.Paused);
    }

    /// <summary>
    /// An application pausing the engine for the length of its startup has not paused the session, even
    /// if it goes no further than that.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task APauseBeforeInitializing_IsNotSaved()
    {
        await using (var engine = NewEngine())
        {
            await engine.PauseAsync(Token);
            await engine.InitializeAsync(Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.False(restored.IsPaused);
        Assert.False(restored.SessionRestoredPaused);
    }

    [Fact(Timeout = 30000)]
    public async Task ASavedPause_IsNotReplacedByAResumeBeforeInitializing()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.PauseAsync(Token);
        }

        await using (var early = NewEngine())
        {
            await early.PauseAsync(Token);
            await early.ResumeAsync(Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.True(restored.IsPaused);
    }

    [Fact(Timeout = 30000)]
    public async Task AnUnreadableSavedPause_CostsNothingAndIsReplaced()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(NewMetadata("kept.bin"), new AddTorrentOptions(_root), Token);
        }

        await File.WriteAllTextAsync(Path.Combine(_root, "session", "session.json"), "{ not json", Token);

        await using (var damaged = await CreateEngineAsync())
        {
            Assert.False(damaged.IsPaused);
            Assert.Single(damaged.GetTorrents());
            await damaged.PauseAsync(Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.True(restored.IsPaused);
    }

    /// <summary>
    /// How an application that will not wait for a file check adds a torrent: stopped, then started once
    /// the add returns. A crash before the next periodic save must not lose the start.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AStartAndAStop_AreSavedAsTheyHappen()
    {
        await using var engine = await CreateEngineAsync();
        var torrent = await engine.AddTorrentAsync(NewMetadata("started.bin"), new AddTorrentOptions(_root) { StartImmediately = false }, Token);
        Assert.False((await LoadEntryAsync()).Options?.WasStarted);

        await torrent.StartAsync(Token);
        Assert.True((await LoadEntryAsync()).Options?.WasStarted);

        await torrent.StopAsync(Token);
        Assert.False((await LoadEntryAsync()).Options?.WasStarted);
    }

    [Fact(Timeout = 30000)]
    public async Task ASessionResumedBeforeClosing_ComesBackRunning()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(NewMetadata("resumed.bin"), new AddTorrentOptions(_root), Token);
            await engine.PauseAsync(Token);
            await engine.ResumeAsync(Token);
        }

        await using var restored = await CreateEngineAsync();

        Assert.False(restored.IsPaused);
        Assert.True(Assert.Single(restored.GetTorrents()).Started);
    }

    [Fact(Timeout = 30000)]
    public async Task ATorrentCheckingItsFiles_StartsAgain()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(CreateRoot(), "checking.bin"), payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("checking.bin").WithPieceLength(PieceLength).AddFile("checking.bin", payload).Build();

        var engine = await CreateEngineAsync(diskReadLimit: 64 * 1024);
        var add = engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root), Token);
        await UntilAsync(() => engine.GetTorrents().Any(t => t.State == TorrentState.CheckingFiles));
        await engine.DisposeAsync();
        await add;

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());

        Assert.True(back.Started);
        Assert.Equal(1.0f, back.Progress);
    }

    /// <summary>
    /// Renaming needs a stopped torrent, so this is the only state a rename ever happens in. Lost, the
    /// torrent looks for the old name at its next start, finds nothing and downloads it all again.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ARenameOfAStoppedTorrent_Survives_AndKeepsItsProgress()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(16 * PieceLength);
        await File.WriteAllBytesAsync(Path.Combine(CreateRoot(), "named.bin"), payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("named.bin").WithPieceLength(PieceLength).AddFile("named.bin", payload).Build();
        await using (var engine = await CreateEngineAsync())
        {
            var torrent = await engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root), Token);
            Assert.Equal(1.0f, torrent.Progress);
            torrent.QueueAutoStart = false;
            await torrent.StopAsync(Token);
            await torrent.RenameFileAsync(0, "renamed.bin", Token);
        }

        // Twice, because a torrent that has only been restored has to save what it restored.
        await using (var engine = await CreateEngineAsync())
        {
            Assert.Single(engine.GetTorrents());
        }

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());
        await back.StartAsync(Token);

        Assert.Equal(1.0f, back.Progress);
        Assert.Equal("renamed.bin", back.GetRenamedFiles()[0]);
        Assert.False(File.Exists(Path.Combine(_root, "named.bin")));
    }

    [Fact(Timeout = 30000)]
    public async Task AFileSelectionMadeWhenAdding_SurvivesWithoutEverStarting()
    {
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(TwoFiles(), new AddTorrentOptions(_root)
            {
                StartImmediately = false,
                FileSelections = [new FileSelection(true), new FileSelection(false)]
            }, Token);
        }

        await using var restored = await CreateEngineAsync();
        Assert.False(Assert.Single(restored.GetTorrents()).GetFileSelection(1).Selected);
    }

    [Fact(Timeout = 30000)]
    public async Task AFileSelectionChangedAfterStopping_Survives()
    {
        await using (var engine = await CreateEngineAsync())
        {
            var torrent = await engine.AddTorrentAsync(TwoFiles(), new AddTorrentOptions(_root), Token);
            torrent.QueueAutoStart = false;
            await torrent.StopAsync(Token);
            await torrent.SetFileSelectionAsync(1, new FileSelection(false), Token);
        }

        await using var restored = await CreateEngineAsync();
        Assert.False(Assert.Single(restored.GetTorrents()).GetFileSelection(1).Selected);
    }

    /// <summary>
    /// Saving a torrent that never started must not turn "nobody has looked at these files" into
    /// "there is nothing in these files".
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ATorrentThatNeverStarted_StillChecksItsFilesWhenItDoes()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(16 * PieceLength);
        await File.WriteAllBytesAsync(Path.Combine(CreateRoot(), "existing.bin"), payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("existing.bin").WithPieceLength(PieceLength).AddFile("existing.bin", payload).Build();
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root) { StartImmediately = false }, Token);
        }

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());
        await back.StartAsync(Token);

        Assert.Equal(1.0f, back.Progress);
    }

    /// <summary>
    /// A start compares the files with the last saved snapshot of them, to catch files changed behind
    /// the torrent's back. A torrent changes its own files as it downloads, so the snapshot from the
    /// restart is out of date by the time it stops, and a pause does the same stop and start.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StoppingAndStartingAfterARestart_KeepsTheFilesTheTorrentChangedItself()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(16 * PieceLength);
        string path = Path.Combine(CreateRoot(), "growing.bin");
        await File.WriteAllBytesAsync(path, payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("growing.bin").WithPieceLength(PieceLength).AddFile("growing.bin", payload).Build();
        await using (var engine = await CreateEngineAsync())
        {
            await engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root), Token);
            await engine.SaveSessionAsync(Token);
        }

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());
        Assert.True(back.Started);

        // What a write leaves behind, while the torrent is the one running.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await back.StopAsync(Token);
        await back.StartAsync(Token);

        Assert.Equal(1.0f, back.Progress);
    }

    /// <summary>
    /// What 2.2.0-era sessions contain for a torrent stopped before a save caught up with it: a bitfield
    /// claiming nothing, over files that are all there.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ASavedBitfieldClaimingNothing_DoesNotStopTheFilesBeingChecked()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(16 * PieceLength);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("zeros.bin").WithPieceLength(PieceLength).AddFile("zeros.bin", payload).Build();
        await using (var engine = await CreateEngineAsync())
        {
            var torrent = await engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root), Token);
            await engine.SaveSessionAsync(Token);
            torrent.QueueAutoStart = false;
            await torrent.StopAsync(Token);
        }

        await File.WriteAllBytesAsync(Path.Combine(_root, "zeros.bin"), payload, Token);

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());
        await back.StartAsync(Token);

        Assert.Equal(1.0f, back.Progress);
    }

    /// <summary>A file edited while the torrent was stopped is what the snapshot is there to catch.</summary>
    [Fact(Timeout = 30000)]
    public async Task AFileChangedWhileStopped_IsNotTrusted()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(16 * PieceLength);
        string path = Path.Combine(CreateRoot(), "edited.bin");
        await File.WriteAllBytesAsync(path, payload, Token);
        var metadata = new ApiTorrentFileBuilder()
            .WithName("edited.bin").WithPieceLength(PieceLength).AddFile("edited.bin", payload).Build();
        await using (var engine = await CreateEngineAsync())
        {
            var torrent = await engine.AddTorrentAsync(metadata, new AddTorrentOptions(_root), Token);
            torrent.QueueAutoStart = false;
            await torrent.StopAsync(Token);
        }

        payload[0] ^= 0xFF;
        await File.WriteAllBytesAsync(path, payload, Token);

        await using var restored = await CreateEngineAsync();
        var back = Assert.Single(restored.GetTorrents());
        await back.StartAsync(Token);

        // Every piece it claimed is dropped and the file checked, which finds all but the edited one.
        Assert.True(back.Progress is > 0.9f and < 1.0f, $"progress was {back.Progress}");
    }

    /// <summary>
    /// A pause remembers what to start again, and someone stopping one of those torrents afterwards
    /// has changed that.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ATorrentStoppedDuringAPause_StaysStopped()
    {
        await using (var engine = await CreateEngineAsync())
        {
            var stopped = await engine.AddTorrentAsync(NewMetadata("stopped.bin"), new AddTorrentOptions(_root), Token);
            var other = await engine.AddTorrentAsync(NewMetadata("other.bin"), new AddTorrentOptions(_root), Token);
            await engine.PauseAsync(Token);
            await stopped.StopAsync(Token);

            await engine.ResumeAsync(Token);

            Assert.False(stopped.Started);
            Assert.True(other.Started);

            await stopped.StartAsync(Token);
            await engine.PauseAsync(Token);
            await stopped.StopAsync(Token);
        }

        await using var restored = await CreateEngineAsync();
        await restored.ResumeAsync(Token);

        Assert.False(Single(restored, "stopped.bin").Started);
        Assert.True(Single(restored, "other.bin").Started);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateRoot()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    private async Task<ClientEngine> CreateEngineAsync(bool queue = false, long diskReadLimit = 0)
    {
        var engine = NewEngine(queue, diskReadLimit);
        await engine.InitializeAsync(Token);
        return engine;
    }

    private ClientEngine NewEngine(bool queue = false, long diskReadLimit = 0) =>
        ClientEngine.Create(new TorrentClientOptions
        {
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = CreateRoot(), MaxDiskReadSpeed = diskReadLimit },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false },
                Queue = { Enabled = queue, MaxActiveDownloads = 1, RecheckIntervalSeconds = 1 },
                Session = { Enabled = true, SessionPath = Path.Combine(_root, "session") }
            }
        });

    private async Task<SavedTorrentEntry> LoadEntryAsync() =>
        Assert.Single(await new SessionPersistence(Path.Combine(_root, "session"), NullLogger<SessionPersistence>.Instance).LoadAllAsync(Token));

    private Task<SavedSessionState?> LoadSessionStateAsync() =>
        new SessionPersistence(Path.Combine(_root, "session"), NullLogger<SessionPersistence>.Instance).LoadSessionStateAsync(Token);

    private static TorrentFile NewMetadata(string fileName) => new ApiTorrentFileBuilder()
        .WithName(fileName)
        .WithPieceLength(PieceLength)
        .AddFile(fileName, RandomNumberGenerator.GetBytes(PieceLength))
        .Build();

    private static TorrentFile TwoFiles() => new ApiTorrentFileBuilder()
        .WithName("two")
        .WithPieceLength(PieceLength)
        .AddFile("first.bin", RandomNumberGenerator.GetBytes(2 * PieceLength))
        .AddFile("second.bin", RandomNumberGenerator.GetBytes(2 * PieceLength))
        .Build();

    private static ITorrent Single(ClientEngine engine, string name) =>
        Assert.Single(engine.GetTorrents(), t => t.Name == name);

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(10, Token);
        }
    }
}
