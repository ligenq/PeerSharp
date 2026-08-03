using System.Diagnostics;
using System.Text;
using PeerSharp.Interfaces;

namespace PeerSharp.Cli;

/// <summary>
/// Prints what the engine is doing, and - when asked - what it is costing.
///
/// <para>
/// The diagnostics half exists because measuring PeerSharp inside a test host measures the test host:
/// its allocations, its parallel collections, and a GC shaped by the runner. The numbers here come
/// from a process that is doing nothing else.
/// </para>
/// </summary>
internal sealed class Reporter(IClientEngine engine, ITorrent torrent, Options options)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastDownloaded;
    private long _lastUploaded;
    private TimeSpan _lastSample;

    /// <summary>Managed heap when the first report ran, so growth is visible rather than absolute size.</summary>
    private long _baselineHeap;

    private long _peakHeap;

    public void ReportOnce()
    {
        var stats = engine.GetStats();
        long downloaded = torrent.FileTransfer.Downloaded;
        long uploaded = torrent.FileTransfer.Uploaded;

        var elapsed = _clock.Elapsed - _lastSample;
        double downRate = elapsed.TotalSeconds <= 0 ? 0 : (downloaded - _lastDownloaded) / elapsed.TotalSeconds;
        double upRate = elapsed.TotalSeconds <= 0 ? 0 : (uploaded - _lastUploaded) / elapsed.TotalSeconds;
        _lastDownloaded = downloaded;
        _lastUploaded = uploaded;
        _lastSample = _clock.Elapsed;

        var line = new StringBuilder();
        line.Append($"[{_clock.Elapsed.TotalSeconds,6:F0}s] ");
        // MetadataDownload is only present while a magnet is still fetching its metadata; a torrent
        // added from a file has none, and asks about progress instead.
        var metadata = torrent.MetadataDownload;
        line.Append(torrent.HasMetadata || metadata is null
            ? $"{torrent.Progress,7:P1} "
            : $"metadata {metadata.Progress,5:P0} ");
        line.Append($"peers={torrent.Peers.ConnectedCount,3} ");
        line.Append($"down={Rate(downRate)} up={Rate(upRate)} ");
        line.Append($"state={torrent.State}");
        Console.WriteLine(line.ToString());

        if (options.Diagnostics)
        {
            ReportDiagnostics(stats);
        }
    }

    private void ReportDiagnostics(Core.EngineStats stats)
    {
        // Not forcing a collection: the point is what the process actually holds while running, and a
        // forced gen2 on every report would both distort that and change the allocation behaviour
        // being measured.
        long heap = GC.GetTotalMemory(forceFullCollection: false);
        if (_baselineHeap == 0)
        {
            _baselineHeap = heap;
        }

        _peakHeap = Math.Max(_peakHeap, heap);

        var info = GC.GetGCMemoryInfo();

        Console.WriteLine(
            $"          heap={Bytes(heap)} peak={Bytes(_peakHeap)} since-start={Bytes(heap - _baselineHeap)} " +
            $"committed={Bytes(info.TotalCommittedBytes)} pinned={info.PinnedObjectsCount}");
        Console.WriteLine(
            $"          gc gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
            $"pause={info.PauseTimePercentage:F2}% alloc={Bytes(GC.GetTotalAllocatedBytes(precise: false))}");
        Console.WriteLine(
            $"          engine torrents={stats.TorrentCount} active={stats.ActiveTorrents} peers={stats.TotalPeers} " +
            $"threads={Process.GetCurrentProcess().Threads.Count} " +
            $"workingSet={Bytes(Environment.WorkingSet)}");
    }

    public void ReportFinal()
    {
        Console.WriteLine();
        Console.WriteLine($"Ran for        : {_clock.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Downloaded     : {Bytes(torrent.FileTransfer.Downloaded)}");
        Console.WriteLine($"Uploaded       : {Bytes(torrent.FileTransfer.Uploaded)}");
        Console.WriteLine($"Progress       : {torrent.Progress:P2}");

        if (options.Diagnostics)
        {
            Console.WriteLine($"Heap peak      : {Bytes(_peakHeap)}");
            Console.WriteLine($"Total allocated: {Bytes(GC.GetTotalAllocatedBytes(precise: true))}");
            Console.WriteLine(
                $"Collections    : gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
        }
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double scaled = value;
        int unit = 0;
        while (Math.Abs(scaled) >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return $"{scaled,7:F1} {units[unit]}";
    }

    private static string Rate(double bytesPerSecond) => $"{Bytes((long)bytesPerSecond).Trim()}/s";
}
