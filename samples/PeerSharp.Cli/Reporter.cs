using Microsoft.Extensions.Logging;
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
internal sealed class Reporter(IClientEngine engine, IReadOnlyList<ITorrent> torrents, Options options, ILogger logger)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long[] _lastDownloaded = new long[torrents.Count];
    private readonly long[] _lastUploaded = new long[torrents.Count];
    private TimeSpan _lastSample;

    /// <summary>Managed heap when the first report ran, so growth is visible rather than absolute size.</summary>
    private long _baselineHeap;

    private long _peakHeap;

    public void ReportOnce()
    {
        var stats = engine.GetStats();
        var elapsed = _clock.Elapsed - _lastSample;
        _lastSample = _clock.Elapsed;

        double totalDown = 0;
        double totalUp = 0;
        int totalPeers = 0;
        var perTorrent = new List<string>(torrents.Count);

        for (int i = 0; i < torrents.Count; i++)
        {
            var torrent = torrents[i];
            long downloaded = torrent.FileTransfer.Downloaded;
            long uploaded = torrent.FileTransfer.Uploaded;

            double downRate = elapsed.TotalSeconds <= 0 ? 0 : (downloaded - _lastDownloaded[i]) / elapsed.TotalSeconds;
            double upRate = elapsed.TotalSeconds <= 0 ? 0 : (uploaded - _lastUploaded[i]) / elapsed.TotalSeconds;
            _lastDownloaded[i] = downloaded;
            _lastUploaded[i] = uploaded;

            totalDown += downRate;
            totalUp += upRate;
            totalPeers += torrent.Peers.ConnectedCount;

            var line = new StringBuilder();
            // MetadataDownload is only present while a magnet is still fetching its metadata; a torrent
            // added from a file has none, and asks about progress instead.
            var metadata = torrent.MetadataDownload;
            line.Append(torrent.HasMetadata || metadata is null
                ? $"{torrent.Progress,7:P1} "
                : $"metadata {metadata.Progress,5:P0} ");
            line.Append($"peers={torrent.Peers.ConnectedCount,3} ");
            line.Append($"down={Rate(downRate)} up={Rate(upRate)} ");
            line.Append($"state={torrent.State,-12} ");
            line.Append(Shorten(torrent.Name));
            perTorrent.Add(line.ToString());
        }

        // With one torrent the summary would repeat the only row, so it is left out; with several it is
        // the number being watched, and the per-torrent rows say which of them is doing the work - or,
        // when the queue is on, which of them has been stopped to let another run.
        var reports = new List<string>(perTorrent.Count + 1);
        if (torrents.Count > 1)
        {
            reports.Add(
                $"[{_clock.Elapsed.TotalSeconds,6:F0}s] {torrents.Count} torrents  peers={totalPeers,3}  " +
                $"down={Rate(totalDown)} up={Rate(totalUp)}");
            reports.AddRange(perTorrent.Select(static row => "          " + row));
        }
        else
        {
            reports.Add($"[{_clock.Elapsed.TotalSeconds,6:F0}s] {perTorrent[0]}");
        }

        foreach (var report in reports)
        {
            Console.WriteLine(report);

            // Into the log as well. A log file that records what the engine did but not how fast it was
            // going cannot answer the question it exists for: the first one collected this way had every
            // connection and request in it and no way to tell which ten seconds were the slow ones.
            if (options.LogPath is not null)
            {
                logger.LogInformation("REPORT {Report}", report);
            }
        }

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

    /// <summary>
    /// Heap after a full blocking collection. Everything else here deliberately avoids forcing one,
    /// but telling a leak from a heap that simply has not been collected needs exactly that.
    /// </summary>
    public void ReportSettledHeap(string label)
    {
        long before = GC.GetTotalMemory(forceFullCollection: false);
        long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        long workingBefore = Environment.WorkingSet;

        // Compacting, and compacting the large object heap with it. A plain collection frees objects
        // but leaves the segments committed, which is the difference between "the heap shrank" and
        // "the process gave memory back to the operating system" - and the second is what anyone
        // watching Task Manager after pressing stop is actually looking at.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        long after = GC.GetTotalMemory(forceFullCollection: true);
        var info = GC.GetGCMemoryInfo();

        Console.WriteLine(
            $"  {label}: heap {Bytes(before)} -> {Bytes(after)}, " +
            $"committed {Bytes(committedBefore)} -> {Bytes(info.TotalCommittedBytes)}, " +
            $"working set {Bytes(workingBefore)} -> {Bytes(Environment.WorkingSet)}");
    }

    public void ReportFinal()
    {
        Console.WriteLine();
        Console.WriteLine($"Ran for        : {_clock.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Downloaded     : {Bytes(torrents.Sum(t => t.FileTransfer.Downloaded))}");
        Console.WriteLine($"Uploaded       : {Bytes(torrents.Sum(t => t.FileTransfer.Uploaded))}");

        foreach (var torrent in torrents)
        {
            Console.WriteLine($"  {torrent.Progress,7:P2}  {Shorten(torrent.Name)}");
        }

        if (options.Diagnostics)
        {
            Console.WriteLine($"Heap peak      : {Bytes(_peakHeap)}");
            Console.WriteLine($"Total allocated: {Bytes(GC.GetTotalAllocatedBytes(precise: true))}");
            Console.WriteLine(
                $"Collections    : gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
        }
    }

    /// <summary>Enough of the name to tell the rows apart without wrapping the line.</summary>
    private static string Shorten(string name)
        => string.IsNullOrEmpty(name) ? "(unnamed)"
            : name.Length <= 34 ? name
            : name[..31] + "...";

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
