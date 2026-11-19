using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public sealed class SessionProcessCrashFaultTests
{
    private const string ChildMode = "PEERSHARP_SESSION_CRASH_MODE";
    private const string ChildPath = "PEERSHARP_SESSION_CRASH_PATH";
    private const string ChildHash = "PEERSHARP_SESSION_CRASH_HASH";
    private const string ChildPhase = "PEERSHARP_SESSION_CRASH_PHASE";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory(Timeout = 60000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(CrashSeeds))]
    public Task AProcessCrashLeavesOneCompleteSessionGeneration(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    public static TheoryData<int> CrashSeeds => new()
    {
        new TheoryDataRow<int>(0).WithTrait("FaultSeed", "0"),
        new TheoryDataRow<int>(1).WithTrait("FaultSeed", "1")
    };

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpSessionCrash-");
        try
        {
            byte[] bytes = new byte[20];
            scenario.Random.NextBytes(bytes);
            var hash = new InfoHash(bytes);
            var persistence = new SessionPersistence(root.FullName, NullLogger<SessionPersistence>.Instance);
            await persistence.SaveAsync(SessionPublicationFaultTests.Entry(hash, 1), ct);

            bool afterSwap = seed != 0;
            scenario.Record(afterSwap ? "kill the writer after the pointer swap" : "kill the writer before the pointer swap");
            using (var writer = StartChild("write", root.FullName, hash, afterSwap))
            {
                var exit = writer.WaitForExitAsync(ct);
                try
                {
                    string ready = Path.Combine(root.FullName, "ready");
                    Task barrier = TorrentTestUtility.WaitUntilAsync(() => File.Exists(ready) || exit.IsCompleted,
                        timeoutMs: (int)Timeout.TotalMilliseconds, because: "the child to reach the publication barrier");
                    await barrier;
                    Assert.False(writer.HasExited, writer.HasExited ? $"Writer exited with code {writer.ExitCode}." : "");
                    Assert.True(File.Exists(ready));
                    writer.Kill();
                    await exit.WaitAsync(Timeout, ct);
                    Assert.NotEqual(0, writer.ExitCode);
                }
                finally
                {
                    await StopChildAsync(writer);
                }
            }

            scenario.Record("read the surviving session from a new process, then save and reopen a third generation");
            using var reader = StartChild("read", root.FullName, hash, afterSwap);
            try
            {
                await reader.WaitForExitAsync(ct).WaitAsync(Timeout, ct);
                Assert.Equal(0, reader.ExitCode);
                Assert.True(File.Exists(Path.Combine(root.FullName, "verified")));
            }
            finally
            {
                await StopChildAsync(reader);
            }
            SessionPublicationFaultTests.AssertEntry(SessionPublicationFaultTests.Entry(hash, 3),
                Assert.Single(await persistence.LoadAllAsync(ct)));
            string entryPath = Path.Combine(root.FullName, "torrents", hash.ToHexStringUpper());
            Assert.Single(Directory.GetDirectories(entryPath, "snapshot-*"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact(Explicit = true, Timeout = 60000)]
    public async Task SessionCrashChild()
    {
        // The isolated runner may also discover this helper when explicitly running every test.
        string? mode = Environment.GetEnvironmentVariable(ChildMode);
        if (mode == null) return;

        string path = Environment.GetEnvironmentVariable(ChildPath)!;
        var hash = new InfoHash(Convert.FromHexString(Environment.GetEnvironmentVariable(ChildHash)!));
        bool afterSwap = Environment.GetEnvironmentVariable(ChildPhase) == "after";
        var ct = TestContext.Current.CancellationToken;
        var logger = NullLogger<SessionPersistence>.Instance;
        if (mode == "write")
        {
            var writer = new SessionPersistence(path, logger)
            {
                OnSnapshotPublication = async (published, token) =>
                {
                    if (published != afterSwap) return;
                    using (File.Create(Path.Combine(path, "ready"))) { }
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                }
            };
            await writer.SaveAsync(SessionPublicationFaultTests.Entry(hash, 2), ct);
            Assert.Fail("The writer must be killed while publication is paused.");
        }
        else
        {
            Assert.Equal("read", mode);
            var reader = new SessionPersistence(path, logger);
            SessionPublicationFaultTests.AssertEntry(SessionPublicationFaultTests.Entry(hash, afterSwap ? (byte)2 : (byte)1),
                Assert.Single(await reader.LoadAllAsync(ct)));
            await reader.SaveAsync(SessionPublicationFaultTests.Entry(hash, 3), ct);
            await File.WriteAllTextAsync(Path.Combine(path, "verified"), "ok", ct);
        }
    }

    private static Process StartChild(string mode, string path, InfoHash hash, bool afterSwap)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(typeof(SessionProcessCrashFaultTests).Assembly.Location);
        start.ArgumentList.Add("-noLogo");
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(SessionProcessCrashFaultTests).FullName + "." + nameof(SessionCrashChild));
        start.ArgumentList.Add("-explicit");
        start.ArgumentList.Add("on");
        start.Environment[ChildMode] = mode;
        start.Environment[ChildPath] = path;
        start.Environment[ChildHash] = hash.ToHexString();
        start.Environment[ChildPhase] = afterSwap ? "after" : "before";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the session crash worker.");
    }

    private static async Task StopChildAsync(Process child)
    {
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync(CancellationToken.None).WaitAsync(Timeout);
    }
}
