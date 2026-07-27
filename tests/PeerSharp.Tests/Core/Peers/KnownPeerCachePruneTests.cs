using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Pruning the known-peer cache while it is being written to.
///
/// <para>
/// The cache is a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> that
/// connection attempts write to continuously, so pruning always runs against a moving target. Two
/// things went wrong there and both only show up under real churn.
/// </para>
///
/// <para>
/// Copying it with <c>List.AddRange</c> looked harmless but is not: AddRange sees an
/// <c>ICollection</c>, reads <c>Count</c>, calls <c>CopyTo</c>, then advances its size by that
/// original count. ConcurrentDictionary.CopyTo recomputes the count under its own locks, so an entry
/// removed in between leaves default pairs - with a null Value - in the tail of the list, and sorting
/// those dereferences null. And the sort key itself is mutable: a comparison whose answer changes part
/// way through makes the sort throw.
/// </para>
/// </summary>
public class KnownPeerCachePruneTests : IDisposable
{
    private readonly string _path;

    public KnownPeerCachePruneTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpPrune_" + Guid.NewGuid().ToString("N"));
    }

    [Fact(Timeout = 60000)]
    public async Task PruningSurvivesConcurrentChurn()
    {
        var (torrent, manager) = CreateContext();
        try
        {
            // A cap low enough that adding peers forces pruning repeatedly.
            torrent.Settings.MaxKnownPeersCache = 64;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var failures = new List<Exception>();

            // Several writers churning the cache while pruning runs against it.
            var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                var random = new Random(worker);
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var peers = new List<IPEndPoint>(16);
                        for (int i = 0; i < 16; i++)
                        {
                            peers.Add(new IPEndPoint(
                                new IPAddress([10, (byte)worker, (byte)random.Next(256), (byte)random.Next(256)]),
                                random.Next(1024, 65535)));
                        }

                        manager.AddPeers(peers, PeerSourceKind.Tracker, null);
                    }
                }
                catch (OperationCanceledException) { /* Expected at the deadline. */ }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            })).ToArray();

            await Task.WhenAll(writers);

            Assert.True(
                failures.Count == 0,
                $"Churning the known-peer cache threw {failures.Count} exception(s); first was: {failures.FirstOrDefault()}");
        }
        finally
        {
            Cleanup(torrent, manager);
        }
    }

    private (Torrent Torrent, PeerManager Manager) CreateContext()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize;
        metadata.Info.Pieces = [new byte[20]];
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata, _path);
        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new PeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor());

        return (torrent, manager);
    }

    private static void Cleanup(Torrent torrent, PeerManager manager)
    {
        try { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
        try { torrent.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best effort. */ }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
