using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PeerSharp.EndToEnd;

internal static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task WriteAsync(
        string outputDirectory,
        RunManifest manifest,
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(Path.Combine(outputDirectory, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(outputDirectory, "results.json"), results, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "results.csv"), Csv(results), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.md"), Markdown(manifest, results), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string Csv(IEnumerable<BenchmarkResult> results)
    {
        var output = new StringBuilder();
        output.AppendLine("engine,revision,mode,variant,backend,iteration,warmup,size_mib,files,peers,duration_s,download_MBps,upload_MBps,cpu_s,cpu_percent_one_core,peak_working_set_bytes,peak_private_bytes,read_bytes,write_bytes,tester_exit,target_exit,success,error,artifacts");
        foreach (BenchmarkResult result in results)
        {
            string[] values =
            [
                result.Engine, result.EngineRevision, result.Mode, result.Variant, result.Backend,
                result.Iteration.ToString(CultureInfo.InvariantCulture), result.Warmup.ToString(CultureInfo.InvariantCulture),
                result.SizeMiB.ToString(CultureInfo.InvariantCulture), result.FileCount.ToString(CultureInfo.InvariantCulture),
                result.PeerCount.ToString(CultureInfo.InvariantCulture), Format(result.DurationSeconds),
                Format(result.DownloadMBps), Format(result.UploadMBps), Format(result.CpuSeconds),
                Format(result.CpuPercentOneCore), result.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                result.PeakPrivateBytes.ToString(CultureInfo.InvariantCulture), result.ReadBytes.ToString(CultureInfo.InvariantCulture),
                result.WriteBytes.ToString(CultureInfo.InvariantCulture), result.TesterExitCode.ToString(CultureInfo.InvariantCulture),
                result.TargetExitCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                result.Success.ToString(CultureInfo.InvariantCulture), result.Error ?? string.Empty, result.ArtifactDirectory
            ];
            output.AppendLine(string.Join(',', values.Select(Escape)));
        }

        return output.ToString();
    }

    private static string Markdown(RunManifest manifest, IReadOnlyList<BenchmarkResult> results)
    {
        BenchmarkResult[] measured = results.Where(static result => !result.Warmup && result.Success).ToArray();
        var output = new StringBuilder()
            .AppendLine("# PeerSharp versus libtorrent")
            .AppendLine()
            .AppendLine($"Started: `{manifest.StartedAt:O}`  ")
            .AppendLine($"PeerSharp: `{manifest.PeerSharpRevision}`  ")
            .AppendLine($"libtorrent: `{manifest.LibtorrentRevision}`  ")
            .AppendLine($"Workload: {manifest.SizeMiB} MiB, {manifest.FileCount} files, {manifest.PeerCount} peers" +
                (manifest.ChurnPerSecond > 0 ? $", {manifest.ChurnPerSecond} reconnect(s)/s" : "") +
                (manifest.Corrupt ? ", corrupt pieces" : "") + "  ")
            .AppendLine($"Cache policy: {manifest.CachePolicy}")
            .AppendLine()
            .AppendLine("Rates come from the common libtorrent `connection_tester`, not from either engine. CPU and memory cover only the engine-under-test process during the tester's transfer window.")
            .AppendLine()
            .AppendLine("Metadata rows are timed by the harness, from starting the engine to the engine reporting the metadata arrived, against a shared libtorrent `client_test` holding the torrent. That interval includes each runtime's startup, which is a real difference between them and a large share of a fetch this short.")
            .AppendLine()
            .AppendLine("| Engine | Mode | Variant | Backend | n | Download MB/s | Upload MB/s | Seconds to metadata | CPU s | CPU % of one core | Peak working set | Private bytes |")
            .AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (IGrouping<(string Engine, string Mode, string Variant, string Backend), BenchmarkResult> group in measured
            .GroupBy(static result => (result.Engine, result.Mode, result.Variant, result.Backend))
            .OrderBy(static group => group.Key.Mode)
            .ThenBy(static group => group.Key.Variant)
            .ThenBy(static group => group.Key.Engine)
            .ThenBy(static group => group.Key.Backend))
        {
            BenchmarkResult[] rows = group.ToArray();
            bool isMetadata = group.Key.Mode == "metadata";
            output.Append("| ").Append(group.Key.Engine)
                .Append(" | ").Append(group.Key.Mode)
                .Append(" | ").Append(group.Key.Variant)
                .Append(" | ").Append(group.Key.Backend)
                .Append(" | ").Append(rows.Length)
                // A mode that moves no payload has no rate, and a printed zero reads as a measured
                // one. Each column is filled in only for the modes it means something in.
                .Append(" | ").Append(isMetadata ? "-" : Format(Median(rows.Select(static row => row.DownloadMBps))))
                .Append(" | ").Append(isMetadata ? "-" : Format(Median(rows.Select(static row => row.UploadMBps))))
                .Append(" | ").Append(isMetadata
                    ? Format(Median(rows.Select(static row => row.DurationSeconds)))
                    : "-")
                .Append(" | ").Append(Format(Median(rows.Select(static row => row.CpuSeconds))))
                .Append(" | ").Append(Format(Median(rows.Select(static row => row.CpuPercentOneCore))))
                .Append(" | ").Append(Bytes((long)Median(rows.Select(static row => (double)row.PeakWorkingSetBytes))))
                .Append(" | ").Append(Bytes((long)Median(rows.Select(static row => (double)row.PeakPrivateBytes))))
                .AppendLine(" |");
        }

        BenchmarkResult[] failures = results.Where(static result => !result.Success).ToArray();
        if (failures.Length > 0)
        {
            output.AppendLine().AppendLine("## Failed trials").AppendLine();
            foreach (BenchmarkResult failure in failures)
            {
                output.Append("- `").Append(failure.Engine).Append('/').Append(failure.Mode).Append('/')
                    .Append(failure.Variant).Append('/').Append(failure.Backend).Append("`: ")
                    .AppendLine(failure.Error ?? $"tester exit code {failure.TesterExitCode}");
            }
        }

        return output.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"") + '"';

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

        return $"{scaled:0.0} {units[unit]}";
    }
}
