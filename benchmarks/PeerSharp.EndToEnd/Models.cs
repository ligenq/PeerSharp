namespace PeerSharp.EndToEnd;

internal sealed record ToolPaths(
    string Dotnet,
    string Cmake,
    string PeerSharpCli,
    string ClientTest,
    string ConnectionTester,
    string BoostRoot,
    string LibtorrentBuildRoot);

internal sealed record BenchmarkRunSummary(string OutputDirectory, int FailedTrials);

internal sealed record BenchmarkCase(
    string Engine,
    string Mode,
    string Variant,
    string Backend,
    int Iteration,
    bool Warmup)
{
    public string Name => $"{Engine}-{Mode}-{Variant}-{Backend}-{(Warmup ? "warmup" : $"run-{Iteration}")}";
}

internal sealed record BenchmarkResult
{
    public required string Engine { get; init; }
    public required string EngineRevision { get; init; }
    public required string Mode { get; init; }
    public required string Variant { get; init; }
    public required string Backend { get; init; }
    public required int Iteration { get; init; }
    public required bool Warmup { get; init; }
    public required int SizeMiB { get; init; }
    public required int FileCount { get; init; }
    public required int PeerCount { get; init; }
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// Size of the info dictionary, for metadata runs. It is the workload there in the way the
    /// payload size is the workload for a transfer, and it is not derivable from the others: it grows
    /// with the piece count and the file list rather than with the bytes on disk.
    /// </summary>
    public long MetadataBytes { get; init; }
    public required double DownloadMBps { get; init; }
    public required double UploadMBps { get; init; }
    public required double CpuSeconds { get; init; }
    public required double CpuPercentOneCore { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long PeakPrivateBytes { get; init; }
    public required long ReadBytes { get; init; }
    public required long WriteBytes { get; init; }
    public required int TesterExitCode { get; init; }
    public required int? TargetExitCode { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public required string ArtifactDirectory { get; init; }
}

internal sealed record RunManifest
{
    public required DateTimeOffset StartedAt { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Runtime { get; init; }
    public required int LogicalProcessorCount { get; init; }
    public required string MachineName { get; init; }
    public required string PeerSharpRevision { get; init; }
    public required string LibtorrentRevision { get; init; }
    public required string LibtorrentRoot { get; init; }
    public required string ConnectionTester { get; init; }
    public required int SizeMiB { get; init; }
    public required int FileCount { get; init; }
    public required int PeerCount { get; init; }
    public required int Iterations { get; init; }
    public required int Warmups { get; init; }
    public required int RandomSeed { get; init; }
    public required string CachePolicy { get; init; }
}
