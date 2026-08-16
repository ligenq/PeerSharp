using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using System.Text;
using System.Text.Json;

namespace PeerSharp.Tests.Core;

public sealed class SessionPersistenceTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public SessionPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentSessionPersistenceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllData()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());

        var torrentBytes = Encoding.ASCII.GetBytes("torrent-bytes");
        const string magnet = "magnet:?xt=urn:btih:TEST";
        var resume = new TorrentResumeData
        {
            Hash = hash,
            Timestamp = DateTimeOffset.UtcNow,
            Data = [1, 2, 3, 4]
        };
        var options = new SavedTorrentOptions(
            DownloadPath: "C:\\Downloads",
            WasStarted: true,
            DownloadLimitBytesPerSecond: 123,
            UploadLimitBytesPerSecond: 456,
            QueuePriority: 2,
            RatioLimit: 1.5f,
            SeedTimeLimit: TimeSpan.FromMinutes(10),
            DownloadStrategy: DownloadStrategy.Sequential)
        {
            PeerPreferences =
            [
                new SavedPeerPreference("192.0.2.10", 6881, UtpSupported: false),
                new SavedPeerPreference("2001:db8::10", 6882, OfferEncryptionNext: false)
            ]
        };

        var entry = new SavedTorrentEntry(hash, torrentBytes, magnet, resume, options);
        await persistence.SaveAsync(entry);

        var reloaded = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var entries = await reloaded.LoadAllAsync();

        Assert.Single(entries);
        var loaded = entries[0];
        Assert.Equal(hash, loaded.Hash);
        Assert.Equal(torrentBytes, loaded.TorrentFileData);
        Assert.Equal(magnet, loaded.MagnetLink);
        Assert.NotNull(loaded.ResumeData);
        Assert.Equal(resume.Data, loaded.ResumeData?.Data);
        Assert.NotNull(loaded.Options);
        Assert.Equal(options.DownloadPath, loaded.Options?.DownloadPath);
        Assert.Equal(options.WasStarted, loaded.Options?.WasStarted);
        Assert.Equal(options.DownloadLimitBytesPerSecond, loaded.Options?.DownloadLimitBytesPerSecond);
        Assert.Equal(options.UploadLimitBytesPerSecond, loaded.Options?.UploadLimitBytesPerSecond);
        Assert.Equal(options.QueuePriority, loaded.Options?.QueuePriority);
        Assert.Equal(options.RatioLimit, loaded.Options?.RatioLimit);
        Assert.Equal(options.SeedTimeLimit, loaded.Options?.SeedTimeLimit);
        Assert.Equal(options.DownloadStrategy, loaded.Options?.DownloadStrategy);
        Assert.Equal(options.PeerPreferences, loaded.Options?.PeerPreferences);
    }

    [Fact]
    public async Task LoadAllAsync_InvalidOptionsJson_DoesNotDropEntry()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0xAB, 20).ToArray());

        var entryDir = Path.Combine(_tempDir, "torrents", hash.ToHexStringUpper());
        Directory.CreateDirectory(entryDir);

        await File.WriteAllBytesAsync(Path.Combine(entryDir, "torrent.torrent"), [9, 8, 7]);
        await File.WriteAllTextAsync(Path.Combine(entryDir, "options.json"), "{not valid json");

        var entries = await persistence.LoadAllAsync();
        Assert.Single(entries);
        Assert.Equal(hash, entries[0].Hash);
        Assert.Null(entries[0].Options);
    }

    [Fact]
    public async Task LoadAllAsync_SkipsEntriesWithoutTorrentOrMagnet()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x11, 20).ToArray());

        var entryDir = Path.Combine(_tempDir, "torrents", hash.ToHexStringUpper());
        Directory.CreateDirectory(entryDir);
        await File.WriteAllTextAsync(Path.Combine(entryDir, "options.json"), JsonSerializer.Serialize(new SavedTorrentOptions()));

        var entries = await persistence.LoadAllAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntryDirectory()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x22, 20).ToArray());

        var entry = new SavedTorrentEntry(hash, [1, 2, 3]);
        await persistence.SaveAsync(entry);

        var entryDir = Path.Combine(_tempDir, "torrents", hash.ToHexStringUpper());
        Assert.True(Directory.Exists(entryDir));

        await persistence.DeleteAsync(hash);
        Assert.False(Directory.Exists(entryDir));
    }

    [Fact]
    public async Task SaveAllAsync_MultipleTorrents_SavesEachEntry()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);

        var hash1 = new InfoHash(Enumerable.Repeat((byte)0x11, 20).ToArray());
        var hash2 = new InfoHash(Enumerable.Repeat((byte)0x22, 20).ToArray());
        var hash3 = new InfoHash(Enumerable.Repeat((byte)0x33, 20).ToArray());

        var entries = new[]
        {
            new SavedTorrentEntry(hash1, [1, 2, 3], "magnet:?xt=urn:btih:HASH1"),
            new SavedTorrentEntry(hash2, [4, 5, 6], "magnet:?xt=urn:btih:HASH2"),
            new SavedTorrentEntry(hash3, [7, 8, 9], "magnet:?xt=urn:btih:HASH3")
        };

        await persistence.SaveAllAsync(entries);

        var loaded = await persistence.LoadAllAsync();
        Assert.Equal(3, loaded.Count);

        var byHash = loaded.ToDictionary(e => e.Hash);
        Assert.Equal(new byte[] { 1, 2, 3 }, byHash[hash1].TorrentFileData);
        Assert.Equal("magnet:?xt=urn:btih:HASH2", byHash[hash2].MagnetLink);
        Assert.Equal(new byte[] { 7, 8, 9 }, byHash[hash3].TorrentFileData);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSameHash_PublishesOneCompleteEntryAndNoTemporaryFiles()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x44, 20).ToArray());
        byte[] first = Enumerable.Repeat((byte)0x11, 128 * 1024).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x22, 128 * 1024).ToArray();

        Task[] saves = Enumerable.Range(0, 12).Select(i => persistence.SaveAsync(
            i % 2 == 0
                ? new SavedTorrentEntry(hash, first, "magnet:first")
                : new SavedTorrentEntry(hash, second, "magnet:second"))).ToArray();
        await Task.WhenAll(saves);

        var loaded = Assert.Single(await persistence.LoadAllAsync());
        bool isFirst = loaded.TorrentFileData!.SequenceEqual(first);
        bool isSecond = loaded.TorrentFileData.SequenceEqual(second);
        Assert.True(isFirst || isSecond);
        Assert.Equal(isFirst ? "magnet:first" : "magnet:second", loaded.MagnetLink);
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact(Timeout = 30000)]
    public async Task SaveAsync_CancelledBeforeAcquiringEntry_PreservesPublishedFile()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x55, 20).ToArray());
        byte[] published = Enumerable.Repeat((byte)0x19, 1024).ToArray();
        await persistence.SaveAsync(new SavedTorrentEntry(hash, published));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        byte[] replacement = Enumerable.Repeat((byte)0xe3, 1024).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistence.SaveAsync(new SavedTorrentEntry(hash, replacement), cts.Token));

        var loaded = Assert.Single(await persistence.LoadAllAsync());
        Assert.Equal(published, loaded.TorrentFileData);
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact(Timeout = 30000)]
    public async Task SaveAsync_CancelledMidWrite_NeverPublishesTornDataOrLeavesTemporaryFiles()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x77, 20).ToArray());
        byte[] published = Enumerable.Repeat((byte)0x19, 4 * 1024 * 1024).ToArray();
        await persistence.SaveAsync(new SavedTorrentEntry(hash, published, "magnet:published"));

        byte[] replacement = Enumerable.Repeat((byte)0xe3, 4 * 1024 * 1024).ToArray();
        using var cts = new CancellationTokenSource();
        Task save = persistence.SaveAsync(new SavedTorrentEntry(hash, replacement, "magnet:replacement"), cts.Token);
        await cts.CancelAsync();

        try
        {
            await save;
        }
        catch (OperationCanceledException)
        {
            // Either outcome is legal - the point is that neither leaves a torn entry behind.
        }

        // Whichever payload won, it must be complete: the temporary-file + rename dance exists
        // precisely so a cancelled write can never publish a half-written file.
        var loaded = Assert.Single(await persistence.LoadAllAsync());
        byte[] stored = Assert.IsType<byte[]>(loaded.TorrentFileData);
        Assert.True(
            stored.SequenceEqual(published) || stored.SequenceEqual(replacement),
            $"Published a torn entry of {stored.Length} bytes.");
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact(Timeout = 30000)]
    public async Task EntryGates_AreEvictedOnceIdle()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        byte[] payload = Enumerable.Repeat((byte)0x2a, 256).ToArray();

        // Churn distinct hashes: an un-evicted gate map would retain one semaphore per hash
        // this session has ever touched.
        for (int i = 0; i < 50; i++)
        {
            var hash = new InfoHash(Enumerable.Repeat((byte)i, 20).ToArray());
            await persistence.SaveAsync(new SavedTorrentEntry(hash, payload));
            await persistence.DeleteAsync(hash);
        }

        var gatesField = typeof(SessionPersistence).GetField(
            "_entryGates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var gates = (System.Collections.ICollection)gatesField!.GetValue(persistence)!;
        Assert.Empty(gates);
    }

    [Fact(Timeout = 30000)]
    public async Task EntryGates_AreEvictedAfterConcurrentSameHashOperations()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x88, 20).ToArray());
        byte[] payload = Enumerable.Repeat((byte)0x5b, 64 * 1024).ToArray();
        var entry = new SavedTorrentEntry(hash, payload, "magnet:concurrent");

        // Overlapping renters must hand the gate back exactly once each; an off-by-one in the
        // reference count would either leak the gate forever or evict it while still in use.
        await Task.WhenAll(Enumerable.Range(0, 24)
            .SelectMany(_ => new[] { persistence.SaveAsync(entry), persistence.DeleteAsync(hash) }));

        var gatesField = typeof(SessionPersistence).GetField(
            "_entryGates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var gates = (System.Collections.ICollection)gatesField!.GetValue(persistence)!;
        Assert.Empty(gates);

        // The gate still has to work after all that churn.
        await persistence.SaveAsync(entry);
        var loaded = Assert.Single(await persistence.LoadAllAsync());
        Assert.Equal(payload, loaded.TorrentFileData);
    }

    [Fact(Timeout = 30000)]
    public async Task SaveAsync_AndDeleteAsync_SameHash_NeverPublishPartialEntry()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0x66, 20).ToArray());
        byte[] payload = Enumerable.Repeat((byte)0x4d, 512 * 1024).ToArray();
        var entry = new SavedTorrentEntry(hash, payload, "magnet:complete");

        Task[] operations = Enumerable.Range(0, 24)
            .SelectMany(_ => new[] { persistence.SaveAsync(entry), persistence.DeleteAsync(hash) })
            .ToArray();
        await Task.WhenAll(operations);

        IReadOnlyList<SavedTorrentEntry> loaded = await persistence.LoadAllAsync();
        if (loaded.Count != 0)
        {
            var saved = Assert.Single(loaded);
            Assert.Equal(payload, saved.TorrentFileData);
            Assert.Equal("magnet:complete", saved.MagnetLink);
        }
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveAllAsync_EmptyEnumerable_NoEntriesSaved()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);

        await persistence.SaveAllAsync(Array.Empty<SavedTorrentEntry>());

        var loaded = await persistence.LoadAllAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task SaveAllAsync_NullEntries_ThrowsArgumentNullException()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            persistence.SaveAllAsync(null!));
    }

    [Fact]
    public async Task SaveAllAsync_CancelledToken_ThrowsBeforeSavingAnything()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);

        var hash = new InfoHash(Enumerable.Repeat((byte)0xAA, 20).ToArray());
        var entries = new[] { new SavedTorrentEntry(hash, [1, 2, 3]) };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            persistence.SaveAllAsync(entries, cts.Token));

        // Cancellation fires before SaveAsync is reached — no entry should have been written
        var loaded = await persistence.LoadAllAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task SaveAndLoad_DhtState_RoundTripsData()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var nodeId = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();

        var nodes = new List<DhtNode>
        {
            new DhtNode(
                Enumerable.Repeat((byte)0xAB, 20).ToArray(),
                new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 6881)),
            new DhtNode(
                Enumerable.Repeat((byte)0xCD, 20).ToArray(),
                new System.Net.IPEndPoint(System.Net.IPAddress.Parse("2001:db8::1"), 51413))
        };

        var state = new DhtState(nodeId, nodes);
        await persistence.SaveDhtStateAsync(state);

        var reloaded = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var loaded = await reloaded.LoadDhtStateAsync();

        Assert.NotNull(loaded);
        Assert.Equal(nodeId, loaded!.NodeId);
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.Equal(nodes[0].Id, loaded.Nodes[0].Id);
        Assert.Equal(nodes[0].EndPoint, loaded.Nodes[0].EndPoint);
        Assert.Equal(nodes[1].Id, loaded.Nodes[1].Id);
        Assert.Equal(nodes[1].EndPoint, loaded.Nodes[1].EndPoint);
    }
}




