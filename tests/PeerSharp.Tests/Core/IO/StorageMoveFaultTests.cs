using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public sealed class StorageMoveFaultTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private const int PieceCount = 4;
    private static readonly FieldInfo StorageField = typeof(Files).GetField("_storage",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task InterruptedMoveRollsBackAndCanBeRetriedAfterReopen(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpMoveFault-");
        try
        {
            await RunInDirectoryAsync(root.FullName, seed, scenario, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string root, int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        string original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        var persistence = new SessionPersistence(Path.Combine(root, "session"), NullLogger<SessionPersistence>.Instance);
        byte[] payload = new byte[PieceCount * PieceSize];
        scenario.Random.NextBytes(payload);
        int boundary = PieceSize + scenario.Random.Next(1, PieceSize);
        byte[][] files = [payload[..boundary], payload[boundary..(3 * PieceSize - 7)], payload[(3 * PieceSize - 7)..]];
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("move-fault").WithPieceLength(PieceSize)
            .AddFile("a.bin", files[0]).AddFile("b.bin", files[1]).AddFile("c.bin", files[2]).Build();
        string[] paths = file.Metadata.Info.Files.Select(entry => entry.Path).ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = Path.Combine(original, paths[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, files[i], ct);
        }

        using (var handles = new FileHandleCache())
        await using (var torrent = TorrentTestUtility.CreateMinimal(file.Metadata, original, fileHandleCache: handles))
        {
            Assert.Equal(PieceCount, await torrent.ForceRecheckAsync(cancellationToken: ct));
            await SaveAsync(torrent, persistence, file, ct);

            int failAt = 1 + ((seed >> 1) & 1);
            bool cancel = (seed & 1) == 0;
            string blocked = Path.Combine(destination, paths[failAt]);
            if (!cancel) Directory.CreateDirectory(blocked);

            using var moveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var storage = (Storage)StorageField.GetValue(torrent.FilesInternal)!;
            var rolledBack = new List<string>();
            int forwardMoves = 0;
            storage.BeforeFileMoveForTesting = from =>
            {
                string relative = Path.GetRelativePath(destination, from);
                if (paths.Contains(relative))
                {
                    rolledBack.Add(relative);
                    return;
                }

                int index = forwardMoves++;
                Assert.Equal(Path.Combine(original, paths[index]), from);
                if (index == 0)
                {
                    throw new IOException("Injected cross-volume rename failure");
                }
                if (index == failAt)
                {
                    Assert.True(File.Exists(Path.Combine(destination, paths[0])));
                    Assert.False(File.Exists(Path.Combine(original, paths[0])));
                    if (cancel)
                    {
                        moveCancellation.Cancel();
                        // The failed rename enters the real, cancellable copy fallback.
                        throw new IOException("Injected rename failure before copy cancellation");
                    }
                }
            };

            scenario.Record($"force the first file through copy fallback; {(cancel ? "cancel" : "block")} file {failAt + 1}");
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => torrent.MoveStorageAsync(destination, moveCancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<StorageException>(() => torrent.MoveStorageAsync(destination, ct));
                Directory.Delete(blocked);
            }

            Assert.Equal(failAt + 1, forwardMoves);
            Assert.Equal(paths[..failAt].Reverse(), rolledBack);
            Assert.Equal(original, torrent.FilesInternal.DownloadPath);
            Assert.Empty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
            await AssertDiskAsync(original, paths, files, ct);
            scenario.Record("save after rollback and reopen using the persisted path and bitfield");
            await SaveAsync(torrent, persistence, file, ct);
        }

        using (var handles = new FileHandleCache())
        await using (var restored = await ReopenAsync(persistence, original, handles, ct))
        {
            scenario.Record("retry from the restored torrent and save its new download path");
            await restored.MoveStorageAsync(destination, ct);
            Assert.Equal(destination, restored.FilesInternal.DownloadPath);
            Assert.Empty(Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories));
            await AssertDiskAsync(destination, paths, files, ct);
            await SaveAsync(restored, persistence, file, ct);
        }

        scenario.Record("reopen the successful move and rehash the claimed pieces at the new path");
        using var finalHandles = new FileHandleCache();
        await using var moved = await ReopenAsync(persistence, destination, finalHandles, ct);
    }

    private static async Task SaveAsync(Torrent torrent, SessionPersistence persistence, TorrentFile file, CancellationToken ct)
    {
        var registry = new TorrentRegistry();
        registry.Add(torrent);
        await using var session = new SessionManager(persistence, registry, TimeProvider.System, NullLogger<SessionManager>.Instance);
        await session.SaveTorrentEntryAsync(torrent, file.RawData.ToArray(), cancellationToken: ct);
    }

    private static async Task<Torrent> ReopenAsync(SessionPersistence persistence, string expectedPath,
        FileHandleCache handles, CancellationToken ct)
    {
        var saved = Assert.Single(await persistence.LoadAllAsync(ct));
        Assert.NotNull(saved.Options);
        Assert.NotNull(saved.ResumeData);
        Assert.Equal(expectedPath, saved.Options.DownloadPath);
        var metadata = TorrentFile.Parse(saved.TorrentFileData!).Metadata;
        var torrent = TorrentTestUtility.CreateMinimal(metadata, saved.Options.DownloadPath, resumeData: saved.ResumeData,
            fileHandleCache: handles);
        try
        {
            Assert.Equal(PieceCount, torrent.Pieces.ReceivedCount);
            Assert.Equal(PieceCount, await torrent.ForceRecheckAsync(cancellationToken: ct));
            return torrent;
        }
        catch
        {
            await torrent.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertDiskAsync(string root, string[] paths, byte[][] files, CancellationToken ct)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            Assert.Equal(files[i], await File.ReadAllBytesAsync(Path.Combine(root, paths[i]), ct));
        }
        Assert.Equal(paths.Length, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
    }
}
