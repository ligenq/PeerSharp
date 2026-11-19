using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public sealed class SessionPublicationFaultTests
{
    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task ReopenReadsOneCompleteGenerationAcrossThePublicationBoundary(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpSessionFault-");
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
        byte[] data = new byte[20];
        scenario.Random.NextBytes(data);
        var hash = new InfoHash(data);
        var logger = NullLogger<SessionPersistence>.Instance;
        var reader = new SessionPersistence(path, logger);
        var baseline = Entry(hash, 1);
        var next = Entry(hash, 2);
        await reader.SaveAsync(baseline, ct);
        string entryPath = Path.Combine(path, "torrents", hash.ToHexStringUpper());
        string pointer = Path.Combine(entryPath, "current.txt");
        string baselineSnapshot = await File.ReadAllTextAsync(pointer, ct);
        var phases = new[] { false, true };
        scenario.Random.Shuffle(phases);

        foreach (bool interruptAfter in phases)
        {
            scenario.Record(interruptAfter ? "interrupt after the pointer swap" : "interrupt before the pointer swap");
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = new SessionPersistence(path, logger)
            {
                OnSnapshotPublication = async (published, token) =>
                {
                    if (published == interruptAfter)
                    {
                        reached.SetResult();
                        await release.Task.WaitAsync(ct);
                        token.ThrowIfCancellationRequested();
                    }
                }
            };

            Task save = writer.SaveAsync(next, cancellation.Token);
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                AssertEntry(interruptAfter ? next : baseline, Assert.Single(await reader.LoadAllAsync(ct)));
                Assert.Equal(!interruptAfter, baselineSnapshot == await File.ReadAllTextAsync(pointer, ct));
            }
            finally
            {
                await cancellation.CancelAsync();
                release.TrySetResult();
                try { await save.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Assert.True(save.IsCanceled);

            // Use a fresh instance so the result depends only on the published files.
            reader = new SessionPersistence(path, logger);
            AssertEntry(interruptAfter ? next : baseline, Assert.Single(await reader.LoadAllAsync(ct)));
            Assert.Empty(Directory.GetFiles(entryPath, "*.tmp", SearchOption.AllDirectories));
            if (!interruptAfter)
            {
                Assert.Single(Directory.GetDirectories(entryPath, "snapshot-*"));
            }
            await reader.SaveAsync(baseline, ct);
            baselineSnapshot = await File.ReadAllTextAsync(pointer, ct);
            Assert.Single(Directory.GetDirectories(entryPath, "snapshot-*"));
        }
    }

    internal static SavedTorrentEntry Entry(InfoHash hash, byte revision) => new(
        hash,
        TorrentFileData: [revision, 10],
        ResumeData: new TorrentResumeData { Data = [revision, 20] },
        Options: new SavedTorrentOptions(QueuePriority: revision, WasStarted: revision == 2));

    internal static void AssertEntry(SavedTorrentEntry expected, SavedTorrentEntry actual)
    {
        Assert.Equal(expected.Hash, actual.Hash);
        Assert.Equal(expected.TorrentFileData, actual.TorrentFileData);
        Assert.Equal(expected.ResumeData!.Data, actual.ResumeData!.Data);
        Assert.NotNull(actual.Options);
        Assert.Equal(expected.Options!.QueuePriority, actual.Options.QueuePriority);
        Assert.Equal(expected.Options.WasStarted, actual.Options.WasStarted);
    }
}
