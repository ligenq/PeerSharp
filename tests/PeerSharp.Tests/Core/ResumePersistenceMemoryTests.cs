using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core;

// Total allocated bytes spans async continuations. Keep other test collections out of the measurement.
[Collection("Concurrency")]
public sealed class ResumePersistenceMemoryTests
{
    [Fact(Timeout = 30000)]
    public async Task StreamingSaveDoesNotAllocateTheWholeOldOrNewResumeJson()
    {
        string root = NewRoot();
        try
        {
            var persistence = new SessionPersistence(root, NullLogger<SessionPersistence>.Instance);
            var entry = new SavedTorrentEntry(InfoHash.CreateRandom(), [1], Options: new SavedTorrentOptions());
            var state = new TorrentStateData();
            await persistence.SaveAsync(entry, state, TestContext.Current.CancellationToken);
            for (int i = 0; i < 4; i++)
            {
                var data = new byte[4 * 1024 * 1024];
                data.AsSpan().Fill((byte)(i + 1));
                state.UnfinishedPieces.Add(new() { Index = i, Blocks = [true], Data = data });
            }

            for (int save = 0; save < 2; save++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                await persistence.SaveAsync(entry, state, TestContext.Current.CancellationToken);
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                TestContext.Current.TestOutputHelper!.WriteLine($"Save {save + 1}: {allocated:N0} managed bytes allocated for >20 MiB JSON.");
                Assert.True(allocated < 8 * 1024 * 1024, $"Streaming allocated {allocated:N0} bytes for an already captured 16 MiB state.");
            }

            // Public persistence consumers and older readers still receive the same JSON format.
            var saved = Assert.Single(await persistence.LoadAllAsync(TestContext.Current.CancellationToken));
            Assert.True(saved.ResumeData!.Data.Length > 20 * 1024 * 1024);
            using var json = JsonDocument.Parse(saved.ResumeData.Data);
            for (int i = 0; i < 4; i++)
                Assert.Equal(state.UnfinishedPieces[i].Data, json.RootElement.GetProperty("UnfinishedPieces")[i].GetProperty("Data").GetBytesFromBase64());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(Timeout = 30000)]
    public async Task UpdatingOptionsCopiesLargeResumeDataWithBoundedManagedMemory()
    {
        string root = NewRoot();
        try
        {
            var persistence = new SessionPersistence(root, NullLogger<SessionPersistence>.Instance);
            var hash = InfoHash.CreateRandom();
            byte[] data = new byte[20 * 1024 * 1024];
            new Random(17).NextBytes(data);
            var entry = new SavedTorrentEntry(hash, [1, 2], "magnet:original", new TorrentResumeData { Data = data });
            await persistence.SaveAsync(entry, TestContext.Current.CancellationToken);
            // Warm the copy path before measuring it.
            var update = new SavedTorrentEntry(hash, Options: new SavedTorrentOptions(QueuePriority: 9));
            await persistence.SaveAsync(update, TestContext.Current.CancellationToken);

            long before = GC.GetTotalAllocatedBytes(precise: true);
            await persistence.SaveAsync(update, TestContext.Current.CancellationToken);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper!.WriteLine($"Options update: {allocated:N0} managed bytes allocated while preserving 20 MiB resume data.");
            Assert.True(allocated < 8 * 1024 * 1024, $"Copying an unchanged resume file allocated {allocated:N0} bytes.");

            var saved = Assert.Single(await persistence.LoadAllAsync(TestContext.Current.CancellationToken));
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(saved.ResumeData!.Data));
            Assert.Equal(new byte[] { 1, 2 }, saved.TorrentFileData);
            Assert.Equal("magnet:original", saved.MagnetLink);
            Assert.Equal(9, saved.Options!.QueuePriority);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(Timeout = 30000)]
    public async Task FailedStreamingWriteKeepsThePreviousCompleteSnapshotAndCleansUp()
    {
        string root = NewRoot();
        try
        {
            var persistence = new SessionPersistence(root, NullLogger<SessionPersistence>.Instance);
            var hash = InfoHash.CreateRandom();
            await persistence.SaveAsync(new SavedTorrentEntry(hash, [1], "magnet:old",
                new TorrentResumeData { Data = [42] }, new SavedTorrentOptions(QueuePriority: 1)), TestContext.Current.CancellationToken);
            var store = DeferredPieceStore.Create([new() { Index = 0, Blocks = [true], Data = [1, 2, 3] }]);
            var captured = store.Capture(streamed: true);
            store.Dispose();
            captured.Lease!.Dispose();
            var broken = new TorrentStateData { UnfinishedPieces = captured.Pieces };

            await Assert.ThrowsAsync<ObjectDisposedException>(() => persistence.SaveAsync(
                new SavedTorrentEntry(hash, [2], "magnet:new", Options: new SavedTorrentOptions(QueuePriority: 2)),
                broken, TestContext.Current.CancellationToken));

            var saved = Assert.Single(await persistence.LoadAllAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 1 }, saved.TorrentFileData);
            Assert.Equal(new byte[] { 42 }, saved.ResumeData!.Data);
            Assert.Equal("magnet:old", saved.MagnetLink);
            Assert.Equal(1, saved.Options!.QueuePriority);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "torrents", hash.ToHexStringUpper())));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpResumeMemory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
