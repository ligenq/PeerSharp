using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public sealed class StorageMoveProcessCrashTests
{
    private const string WorkerRoot = "PEERSHARP_STORAGE_MOVE_CRASH_ROOT";
    private const string WorkerPhase = "PEERSHARP_STORAGE_MOVE_CRASH_PHASE";
    private const int PieceSize = ProtocolConstants.BlockSize;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory(Timeout = 60000)]
    [InlineData("mid-move")]
    [InlineData("during-copy")]
    [InlineData("before-publication")]
    [InlineData("after-publication")]
    [Trait("Suite", "ReplayableFaults")]
    public async Task AStorageMoveSurvivesAProcessCrash(string phase)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("PeerSharpStorageMoveCrash-");
        try
        {
            var payload = new byte[3 * PieceSize];
            new Random(8475).NextBytes(payload);
            byte[][] contents = [payload[..997], payload[997..(2 * PieceSize + 11)], payload[(2 * PieceSize + 11)..]];
            var file = new PeerSharp.Core.TorrentFileBuilder().WithName("move-crash").WithPieceLength(PieceSize)
                .AddFile("a.bin", contents[0]).AddFile("b.bin", contents[1]).AddFile("c.bin", contents[2]).Build();
            string[] paths = file.Metadata.Info.Files.Select(entry => entry.Path).ToArray();
            string original = Original(root.FullName);
            string destination = Destination(root.FullName);
            for (int i = 0; i < paths.Length; i++)
            {
                string source = Path.Combine(original, paths[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                await File.WriteAllBytesAsync(source, contents[i], ct);
            }
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "test.torrent"), file.RawData.ToArray(), ct);

            bool killDuringCopy = phase == "during-copy";
            string unrelatedDestination = Path.Combine(destination, paths[1]);
            string unrelatedScratch = Path.Combine(destination, $".peersharp-move-{Guid.NewGuid():N}-1.tmp");
            if (killDuringCopy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(unrelatedDestination)!);
                await File.WriteAllTextAsync(unrelatedDestination, "unrelated destination file", ct);
                await File.WriteAllTextAsync(unrelatedScratch, "unrelated temporary file", ct);
            }

            await KillMoverAsync(root.FullName, phase, paths, ct);
            string? forwardCopy = null;
            string? recoveryCopy = null;
            if (killDuringCopy)
            {
                forwardCopy = await AssertPartialCopyAsync(root.FullName, phase, destination, contents[1], ct);
                Assert.Equal("unrelated destination file", await File.ReadAllTextAsync(unrelatedDestination, ct));
                await KillMoverAsync(root.FullName, "during-recovery", paths, ct);
                Assert.False(File.Exists(forwardCopy), "Recovery left the interrupted forward copy behind.");
                recoveryCopy = await AssertPartialCopyAsync(root.FullName, "during-recovery", original, contents[0], ct);
            }

            await using var restarted = CreateEngine(root.FullName);
            await restarted.InitializeAsync(ct);
            var torrent = Assert.IsType<Torrent>(Assert.Single(restarted.GetTorrents()));
            string expectedRoot = phase == "after-publication" ? destination : original;
            string inactiveRoot = phase == "after-publication" ? original : destination;
            Assert.Equal(expectedRoot, torrent.Files.DownloadPath);
            Assert.False(torrent.Started);
            Assert.Empty(Directory.GetFiles(Path.Combine(root.FullName, "session"), "storage-move.json", SearchOption.AllDirectories));
            for (int i = 0; i < paths.Length; i++)
            {
                string recovered = Path.Combine(expectedRoot, paths[i]);
                Assert.True(File.Exists(recovered), $"Recovery left {paths[i]} outside {expectedRoot}.");
                Assert.Equal(contents[i], await File.ReadAllBytesAsync(recovered, ct));
                if (!killDuringCopy || i != 1) Assert.False(File.Exists(Path.Combine(inactiveRoot, paths[i])));
            }
            if (killDuringCopy)
            {
                Assert.False(File.Exists(recoveryCopy), "The final restart left the interrupted recovery copy behind.");
                Assert.Equal("unrelated destination file", await File.ReadAllTextAsync(unrelatedDestination, ct));
                Assert.Equal("unrelated temporary file", await File.ReadAllTextAsync(unrelatedScratch, ct));
            }
            await torrent.StartAsync(ct);
            Assert.Equal(file.PieceCount, torrent.PiecesReceived);
            Assert.True(torrent.Finished);
        }
        finally
        {
            // Windows can briefly retain a file handle after terminating a writer mid-copy.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    root.Delete(recursive: true);
                    break;
                }
                catch (IOException) when (OperatingSystem.IsWindows() && attempt < 10)
                {
                    await Task.Delay(100, CancellationToken.None);
                }
            }
        }
    }

    private static async Task<string> AssertPartialCopyAsync(string root, string phase, string expectedRoot, byte[] payload,
        CancellationToken ct)
    {
        string path = await File.ReadAllTextAsync(Marker(root, phase), ct);
        Assert.StartsWith(Path.GetFullPath(expectedRoot) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        byte[] copied = await File.ReadAllBytesAsync(path, ct);
        Assert.InRange(copied.Length, 1, payload.Length - 1);
        Assert.Equal(payload[..copied.Length], copied);
        return path;
    }

    private static async Task KillMoverAsync(string root, string phase, string[] paths, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[]
        {
            typeof(StorageMoveProcessCrashTests).Assembly.Location, "-noLogo", "-method",
            typeof(StorageMoveProcessCrashTests).FullName + "." + nameof(StorageMoverWorker), "-explicit", "on"
        }) start.ArgumentList.Add(arg);
        start.Environment[WorkerRoot] = root;
        start.Environment[WorkerPhase] = phase;
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the mover.");
        var stdout = worker.StandardOutput.ReadToEndAsync();
        var stderr = worker.StandardError.ReadToEndAsync();
        try
        {
            try
            {
                string ready = Marker(root, phase);
                await TorrentTestUtility.WaitUntilAsync(() => File.Exists(ready) || worker.HasExited,
                    (int)Deadline.TotalMilliseconds, "the mover to reach the crash barrier");
                ct.ThrowIfCancellationRequested();
                Assert.False(worker.HasExited, "The mover exited before it could be killed.");
                Assert.True(File.Exists(Path.Combine(Destination(root), paths[0])));
                Assert.False(File.Exists(Path.Combine(Original(root), paths[0])));
                bool split = phase is "mid-move" or "during-copy" or "during-recovery";
                Assert.Equal(split, File.Exists(Path.Combine(Original(root), paths[1])));
            }
            finally
            {
                if (!worker.HasExited) worker.Kill();
                await worker.WaitForExitAsync(CancellationToken.None).WaitAsync(Deadline);
                await Task.WhenAll(stdout, stderr).WaitAsync(Deadline);
            }
        }
        catch
        {
            if (stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully)
            {
                TestContext.Current.TestOutputHelper!.WriteLine(await stdout + await stderr);
            }
            throw;
        }
        Assert.NotEqual(0, worker.ExitCode);
    }

    [Fact(Explicit = true, Timeout = 45000)]
    public async Task StorageMoverWorker()
    {
        string? root = Environment.GetEnvironmentVariable(WorkerRoot);
        if (root == null) return;
        var ct = TestContext.Current.CancellationToken;
        string phase = Environment.GetEnvironmentVariable(WorkerPhase)!;
        var file = TorrentFile.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "test.torrent"), ct));
        string[] paths = file.Metadata.Info.Files.Select(entry => entry.Path).ToArray();
        var persistence = new SessionPersistence(Path.Combine(root, "session"), NullLogger<SessionPersistence>.Instance)
        {
            OnPartialRecoveryCopy = phase == "during-recovery" ? (path, token) => PauseAtCopyAsync(root, phase, path, token) : null,
            OnSnapshotPublication = async (published, token) =>
            {
                if (phase is not ("before-publication" or "after-publication") || published != (phase == "after-publication") ||
                    !File.Exists(Path.Combine(Destination(root), paths[^1]))) return;
                using (File.Create(Marker(root, phase))) { }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        await using var engine = CreateEngine(root, persistence);
        await engine.InitializeAsync(ct);
        if (phase == "during-recovery") Assert.Fail("The recovery process should have been killed during copying.");
        var torrent = (Torrent)await engine.AddTorrentAsync(file, new AddTorrentOptions(Original(root))
        {
            StartImmediately = false
        }, ct);
        Assert.Equal(file.PieceCount, await torrent.ForceRecheckAsync(cancellationToken: ct));
        await engine.SaveSessionAsync(ct);

        if (phase is "mid-move" or "during-copy")
        {
            var field = typeof(Files).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var storage = (Storage)field.GetValue(torrent.FilesInternal)!;
            storage.BeforeFileMoveForTesting = from =>
            {
                int index = phase == "mid-move" ? 0 : 1;
                if (from == Path.Combine(Original(root), paths[index])) throw new IOException("Force the copy fallback.");
                if (phase != "mid-move" || from != Path.Combine(Original(root), paths[1])) return;
                using (File.Create(Marker(root, phase))) { }
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
            };
            if (phase == "during-copy")
                storage.OnPartialMoveCopyForTesting = (path, token) => PauseAtCopyAsync(root, phase, path, token);
        }
        await torrent.MoveStorageAsync(Destination(root), ct);
        Assert.Fail("The worker should have been killed at the barrier.");
    }

    private static async Task PauseAtCopyAsync(string root, string phase, string temporary, CancellationToken ct)
    {
        string marker = Marker(root, phase);
        await File.WriteAllTextAsync(marker + ".pending", temporary, ct);
        File.Move(marker + ".pending", marker);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static ClientEngine CreateEngine(string root, SessionPersistence? persistence = null)
        => ClientEngine.Create(new TorrentClientOptions
        {
            SessionPersistence = persistence,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = Original(root) },
                Session = { Enabled = true, SessionPath = Path.Combine(root, "session"), AutoSaveIntervalSeconds = 0 },
                Dht = { Enabled = false },
                Connection =
                {
                    BindAddress = System.Net.IPAddress.Loopback,
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableUtpIn = false,
                    EnableUtpOut = false,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                }
            }
        });

    private static string Original(string root) => Path.Combine(root, "original");
    private static string Destination(string root) => Path.Combine(root, "destination");
    private static string Marker(string root, string phase) => Path.Combine(root, phase + ".ready");
}
