using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public sealed class ResumeFaultTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private const int PieceCount = 4;
    private const int FileBoundary = PieceSize + 997;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task RestartOnlyAdvertisesVerifiedBytesAfterStorageAndSaveFaults(int seed)
        => ReplayableScenario.RunAsync(seed, RunScenarioAsync, TestContext.Current.CancellationToken);

    private static async Task RunScenarioAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpResumeFault-");
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
        string downloadPath = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        string sessionPath = Path.Combine(root, "session");
        byte[] payload = new byte[PieceCount * PieceSize];
        scenario.Random.NextBytes(payload);
        var file = new TorrentFileBuilder()
            .WithName("resume-fault")
            .WithPieceLength(PieceSize)
            .AddFile("a.bin", payload[..FileBoundary])
            .AddFile("b.bin", payload[FileBoundary..])
            .Build();
        var metadata = file.Metadata;
        var persistence = new SessionPersistence(sessionPath, NullLogger<SessionPersistence>.Instance);
        var completed = new HashSet<int>();
        int[] initialPieces = [0, 2, 3];
        scenario.Random.Shuffle(initialPieces);
        int baselinePiece = initialPieces[0];
        int flushFaultPiece = initialPieces[1];

        using (var handles = new FaultingFileHandles())
        await using (var torrent = TorrentTestUtility.CreateMinimal(metadata, downloadPath, fileHandleCache: handles))
        {
            await torrent.StartAsync(ct);
            var registry = new TorrentRegistry();
            registry.Add(torrent);
            await using var session = new SessionManager(persistence, registry, TimeProvider.System, NullLogger<SessionManager>.Instance);

            Task SaveAsync(CancellationToken token) =>
                session.SaveTorrentEntryAsync(torrent, file.RawData.ToArray(), cancellationToken: token);

            await SendValidPieceAsync(torrent, baselinePiece, payload, ct);
            completed.Add(baselinePiece);
            scenario.Record($"verified piece {baselinePiece}; save baseline");
            await SaveAsync(ct);
            var baseline = Assert.Single(await persistence.LoadAllAsync(ct)).ResumeData;
            Assert.NotNull(baseline);

            await SendValidPieceAsync(torrent, flushFaultPiece, payload, ct);
            completed.Add(flushFaultPiece);

            scenario.Record($"verified piece {flushFaultPiece}; cancel the save while the flush holds a file lock");
            using (var saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var barrier = handles.InterruptNextWriteHandle();
                Task saving = SaveAsync(saveCancellation.Token);
                try
                {
                    await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await saveCancellation.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
                }
                finally
                {
                    barrier.Release.TrySetResult();
                }
            }
            Assert.Equal(baseline.Data, Assert.Single(await persistence.LoadAllAsync(ct)).ResumeData!.Data);

            scenario.Record("fail the next flush with an I/O error; check the previous resume bitfield survives");
            var failedFlush = handles.InterruptNextWriteHandle(new IOException("Injected flush failure"));
            failedFlush.Release.SetResult();
            await SaveAsync(ct);
            Assert.True(failedFlush.Entered.Task.IsCompletedSuccessfully);
            Assert.Equal(baseline.Data, Assert.Single(await persistence.LoadAllAsync(ct)).ResumeData!.Data);

            scenario.Record("retry the save");
            await SaveAsync(ct);
            var retried = Assert.Single(await persistence.LoadAllAsync(ct)).ResumeData;
            Assert.NotNull(retried);
            await using (var checkpoint = TorrentTestUtility.CreateMinimal(metadata, downloadPath, resumeData: retried))
            {
                await AssertVerifiedPiecesAsync(checkpoint, file, completed, ct);
            }

            // Piece 1 straddles two files. The second handle is acquired after the first write.
            // Stop or fail there, after the hash has passed but before the complete piece is on disk.
            bool diskFull = (seed & 1) == 0;
            var interruptedWrite = handles.InterruptNextWriteHandle(
                diskFull ? new IOException("Injected disk full", unchecked((int)0x80070070)) : null,
                fileName: "b.bin");
            scenario.Record($"send valid piece 1; interrupt its second file write with {(diskFull ? "disk full" : "stop")}");
            await SendBlockAsync(torrent, 1, payload).WaitAsync(ct);
            try
            {
                await interruptedWrite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.False(torrent.Pieces.HasPiece(1));
                if (diskFull)
                {
                    interruptedWrite.Release.SetResult();
                    await TorrentTestUtility.WaitUntilAsync(() => torrent.State == TorrentState.Stopped,
                        because: "disk-full error stops the torrent");
                    var error = Assert.IsType<TorrentException>(torrent.LastException);
                    Assert.False(Assert.IsType<StorageException>(error.InnerException).IsRecoverable);
                }
                else
                {
                    await torrent.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
            }
            finally
            {
                interruptedWrite.Release.TrySetResult();
            }
            Assert.False(torrent.Pieces.HasPiece(1));

            scenario.Record("save stopped state and dispose twice");
            await torrent.StopAsync(ct);
            await SaveAsync(ct);
            await torrent.DisposeAsync();
        }

        scenario.Record("restore from the on-disk session and hash the claimed pieces");
        var saved = Assert.Single(await persistence.LoadAllAsync(ct));
        Assert.NotNull(saved.ResumeData);
        await using var restored = TorrentTestUtility.CreateMinimal(TorrentFile.Parse(saved.TorrentFileData!).Metadata,
            downloadPath, resumeData: saved.ResumeData);
        await restored.FilesInternal.InitializeAsync([], ct);
        await AssertVerifiedPiecesAsync(restored, file, completed, ct);

        scenario.Record("reopen after the storage error and redownload the missing pieces");
        await restored.StartAsync(ct);
        foreach (int piece in Enumerable.Range(0, PieceCount).Where(piece => !completed.Contains(piece)))
        {
            await SendValidPieceAsync(restored, piece, payload, ct);
        }
        await AssertVerifiedPiecesAsync(restored, file, Enumerable.Range(0, PieceCount).ToHashSet(), ct);
        Assert.True(restored.Finished);
        await restored.StopAsync(ct);
    }

    private static async Task AssertVerifiedPiecesAsync(Torrent torrent, TorrentFile file, HashSet<int> expected, CancellationToken ct)
    {
        // Open the payload files independently so a read cache cannot hide a bad write.
        var disk = new byte[PieceCount * PieceSize];
        int offset = 0;
        for (int i = 0; i < file.FileCount; i++)
        {
            string path = Path.Combine(torrent.FilesInternal.DownloadPath, torrent.GetFileInfo(i).Path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            int length = checked((int)input.Length);
            await input.ReadExactlyAsync(disk.AsMemory(offset, length), ct);
            offset += length;
        }
        Assert.Equal(disk.Length, offset);

        for (int piece = 0; piece < PieceCount; piece++)
        {
            Assert.Equal(expected.Contains(piece), torrent.Pieces.HasPiece(piece));
            if (torrent.Pieces.HasPiece(piece))
            {
                byte[] hash = SHA1.HashData(disk.AsSpan(piece * PieceSize, PieceSize));
                Assert.Equal(file.Metadata.Info.Pieces[piece], hash);
            }
        }
    }

    private static async Task SendValidPieceAsync(Torrent torrent, int piece, byte[] payload, CancellationToken ct)
    {
        await SendBlockAsync(torrent, piece, payload).WaitAsync(ct);
        await TorrentTestUtility.WaitUntilAsync(() => torrent.Pieces.HasPiece(piece), because: $"piece {piece} to be verified");
    }

    private static Task SendBlockAsync(Torrent torrent, int piece, byte[] payload)
    {
        var block = new Block(piece, 0, PieceSize);
        payload.AsSpan(piece * PieceSize, PieceSize).CopyTo(block.Buffer);
        return torrent.FileTransferInternal.WebSeedBlockReceivedAsync(block);
    }

    private sealed class FaultingFileHandles : IFileHandleCache
    {
        private readonly FileHandleCache _inner = new();
        private WriteBarrier? _next;

        public WriteBarrier InterruptNextWriteHandle(IOException? error = null, string? fileName = null)
        {
            var barrier = new WriteBarrier(error, fileName);
            Assert.Null(Interlocked.Exchange(ref _next, barrier));
            return barrier;
        }

        public async ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
        {
            var barrier = Volatile.Read(ref _next);
            if (writable && barrier != null
                && (barrier.FileName == null || Path.GetFileName(path) == barrier.FileName)
                && ReferenceEquals(Interlocked.CompareExchange(ref _next, null, barrier), barrier))
            {
                barrier.Entered.SetResult();
                await barrier.Release.Task.WaitAsync(cancellationToken);
                if (barrier.Error != null) throw barrier.Error;
            }
            return await _inner.GetHandleAsync(path, writable, cancellationToken);
        }

        public void CloseTorrentHandles(string rootPath) => _inner.CloseTorrentHandles(rootPath);
        public void Dispose() => _inner.Dispose();
    }

    private sealed record WriteBarrier(IOException? Error, string? FileName)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
