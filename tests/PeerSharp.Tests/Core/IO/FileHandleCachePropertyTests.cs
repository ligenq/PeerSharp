using CsCheck;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// The lifetime rule the file handle cache exists to get right: a handle in someone's hands is never
/// closed underneath them.
/// </summary>
/// <remarks>
/// <para>
/// Three separate things close handles here - eviction when the cache is full, upgrading a read-only
/// handle to writable, and closing a stopped torrent's files - and each has to notice that a handle
/// may be in use. Get that wrong and a read or write lands on a disposed handle, which is not a
/// corrupted byte somewhere but an exception on the storage path, or worse a handle number that has
/// since been reused for another file.
/// </para>
/// <para>
/// Reference counting is what makes those three cases safe, and reference counting is exactly the
/// kind of thing that holds for the sequences someone wrote down and fails for the fourth one. Hence
/// generated sequences, with the rule checked after every single operation rather than at the end.
/// </para>
/// </remarks>
public class FileHandleCachePropertyTests : IDisposable
{
    /// <summary>
    /// The cache floors its limit at 32 regardless of what it is asked for, so a smaller number here
    /// would silently mean 32 - and with fewer files than that, eviction never runs and the property
    /// tests nothing. Found the hard way: at a nominal limit of 2 with 5 files, deleting the
    /// reference-count check from the eviction path did not fail a single generated sequence.
    /// </summary>
    private const int MaxOpenFiles = 32;

    /// <summary>Comfortably more files than the cache will hold, so eviction is routine.</summary>
    private const int PathCount = 60;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PeerSharpHandleProps", Guid.NewGuid().ToString("N"));

    public FileHandleCachePropertyTests()
    {
        Directory.CreateDirectory(_root);
        for (int i = 0; i < PathCount; i++)
        {
            File.WriteAllBytes(PathAt(i), new byte[16]);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A handle the test deliberately left open can keep the directory alive on Windows.
            // The temp directory is not what is under test.
        }
    }

    [Fact]
    public async Task ALeasedHandleIsNeverClosed()
    {
        await Operations().SampleAsync(async script =>
        {
            using var cache = new FileHandleCache(MaxOpenFiles);
            var leases = new List<IFileHandleLease>();

            try
            {
                foreach (var operation in script)
                {
                    switch (operation.Kind)
                    {
                        case OperationKind.Acquire:
                            leases.Add(await cache.GetHandleAsync(PathAt(operation.Index), operation.Writable, TestContext.Current.CancellationToken));
                            break;

                        case OperationKind.Release when leases.Count > 0:
                            int index = operation.Index % leases.Count;
                            leases[index].Dispose();
                            leases.RemoveAt(index);
                            break;

                        case OperationKind.CloseTorrent:
                            cache.CloseTorrentHandles(_root);
                            break;
                    }

                    foreach (var lease in leases)
                    {
                        Assert.False(lease.Handle.IsClosed, $"a leased handle for {lease.Path} was closed while still held");
                    }
                }

                // Still usable, not merely still open: the point of holding a lease is being able to
                // read through it.
                foreach (var lease in leases)
                {
                    byte[] buffer = new byte[1];
                    RandomAccess.Read(lease.Handle, buffer, 0);
                }
            }
            finally
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }, iter: 200);
    }

    [Fact]
    public async Task TheLastLeaseOfAnOrphanedHandleClosesIt()
    {
        // Upgrading to writable while the read-only handle is in use orphans the old one rather than
        // closing it. Orphaned is not the same as leaked: whoever releases it last owns closing it,
        // or the process accumulates handles for every file it ever upgraded.
        using var cache = new FileHandleCache(MaxOpenFiles);

        var reader = await cache.GetHandleAsync(PathAt(0), writable: false, TestContext.Current.CancellationToken);
        var writer = await cache.GetHandleAsync(PathAt(0), writable: true, TestContext.Current.CancellationToken);

        Assert.False(reader.Handle.IsClosed);
        Assert.NotSame(reader.Handle, writer.Handle);

        reader.Dispose();
        Assert.True(reader.Handle.IsClosed, "the orphaned handle outlived its last lease");

        Assert.False(writer.Handle.IsClosed);
        writer.Dispose();
    }

    private string PathAt(int index) => Path.Combine(_root, $"file{index}.bin");

    private static Gen<Operation[]> Operations()
    {
        // Weighted heavily towards acquiring, because the case that distinguishes a correct
        // eviction from a careless one only arises when the least recently used handle is still
        // leased - which needs more handles held at once than the cache will keep.
        return Gen.Select(
            Gen.OneOfConst(
                OperationKind.Acquire, OperationKind.Acquire, OperationKind.Acquire,
                OperationKind.Acquire, OperationKind.Acquire, OperationKind.Acquire,
                OperationKind.Acquire,
                OperationKind.Release,
                OperationKind.CloseTorrent),
            Gen.Int[0, PathCount - 1],
            Gen.Bool)
            .Select(t => new Operation(t.Item1, t.Item2, t.Item3))
            .Array[60, 120];
    }

    private enum OperationKind
    {
        Acquire,
        Release,
        CloseTorrent
    }

    private readonly record struct Operation(OperationKind Kind, int Index, bool Writable);
}
