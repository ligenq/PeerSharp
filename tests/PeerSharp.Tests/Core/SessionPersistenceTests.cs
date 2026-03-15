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
        var magnet = "magnet:?xt=urn:btih:TEST";
        var resume = new TorrentResumeData
        {
            Hash = hash,
            Timestamp = DateTimeOffset.UtcNow,
            Data = new byte[] { 1, 2, 3, 4 }
        };
        var options = new SavedTorrentOptions(
            DownloadPath: "C:\\Downloads",
            WasStarted: true,
            DownloadLimitBytesPerSecond: 123,
            UploadLimitBytesPerSecond: 456,
            QueuePriority: 2,
            RatioLimit: 1.5f,
            SeedTimeLimit: TimeSpan.FromMinutes(10),
            DownloadStrategy: DownloadStrategy.Sequential);

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
    }

    [Fact]
    public async Task LoadAllAsync_InvalidOptionsJson_DoesNotDropEntry()
    {
        var persistence = new SessionPersistence(_tempDir, NullLogger<SessionPersistence>.Instance);
        var hash = new InfoHash(Enumerable.Repeat((byte)0xAB, 20).ToArray());

        var entryDir = Path.Combine(_tempDir, "torrents", hash.ToHexStringUpper());
        Directory.CreateDirectory(entryDir);

        await File.WriteAllBytesAsync(Path.Combine(entryDir, "torrent.torrent"), new byte[] { 9, 8, 7 });
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

        var entry = new SavedTorrentEntry(hash, new byte[] { 1, 2, 3 });
        await persistence.SaveAsync(entry);

        var entryDir = Path.Combine(_tempDir, "torrents", hash.ToHexStringUpper());
        Assert.True(Directory.Exists(entryDir));

        await persistence.DeleteAsync(hash);
        Assert.False(Directory.Exists(entryDir));
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




