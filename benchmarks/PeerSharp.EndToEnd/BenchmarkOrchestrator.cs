using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PeerSharp.EndToEnd;

internal sealed partial class BenchmarkOrchestrator(BenchmarkOptions options, ToolPaths tools)
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public async Task<BenchmarkRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        string timestamp = _startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // Include the process id so two benchmark invocations started in the same second cannot
        // share fixtures, trial directories and reports. Concurrent validation runs exposed that
        // timestamp-only names let one process overwrite the other's result set.
        string runRoot = Path.Combine(
            options.ArtifactRoot,
            "runs",
            $"{timestamp}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(runRoot);

        string peerSharpRevision = await GitRevisionAsync(options.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        string libtorrentRevision = await GitRevisionAsync(options.LibtorrentRoot, cancellationToken).ConfigureAwait(false);
        var manifest = new RunManifest
        {
            StartedAt = _startedAt,
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            LogicalProcessorCount = Environment.ProcessorCount,
            MachineName = Environment.MachineName,
            PeerSharpRevision = peerSharpRevision,
            LibtorrentRevision = libtorrentRevision,
            LibtorrentRoot = options.LibtorrentRoot,
            ConnectionTester = tools.ConnectionTester,
            SizeMiB = options.SizeMiB,
            FileCount = options.FileCount,
            PeerCount = options.PeerCount,
            Iterations = options.Iterations,
            Warmups = options.Warmups,
            RandomSeed = options.RandomSeed,
            CachePolicy = OperatingSystem.IsWindows()
                ? "warm/uncontrolled OS cache; Windows cache was not evicted"
                : "uncontrolled OS cache; use an external cache-eviction step for cold-cache claims"
        };

        Console.WriteLine($"Run directory: {runRoot}");
        Console.WriteLine($"PeerSharp:      {peerSharpRevision}");
        Console.WriteLine($"libtorrent:     {libtorrentRevision}");

        Dictionary<string, string> fixtures = await CreateFixturesAsync(runRoot, cancellationToken).ConfigureAwait(false);
        List<BenchmarkCase> cases = CreateCases();
        var results = new List<BenchmarkResult>(cases.Count);

        foreach (BenchmarkCase benchmarkCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine($"[{results.Count + 1}/{cases.Count}] {benchmarkCase.Name}");
            string revision = benchmarkCase.Engine == "peersharp" ? peerSharpRevision : libtorrentRevision;
            BenchmarkResult result = await RunCaseAsync(
                runRoot,
                fixtures[benchmarkCase.Variant],
                benchmarkCase,
                revision,
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            await ReportWriter.WriteAsync(runRoot, manifest, results, cancellationToken).ConfigureAwait(false);

            string activeRate = benchmarkCase.Mode switch
            {
                // A metadata fetch has no rate worth printing - the whole of it is a few hundred
                // kilobytes - so the time it took is the result.
                "metadata" => $"{result.DurationSeconds:0.###}s to metadata",
                "upload" => $"{result.UploadMBps:0.###} MB/s up",
                _ => $"{result.DownloadMBps:0.###} MB/s down"
            };
            Console.WriteLine(result.Success
                ? $"  {activeRate}; CPU {result.CpuSeconds:0.###}s; peak WS {Bytes(result.PeakWorkingSetBytes)}"
                : $"  FAILED: {result.Error}");
        }

        int failures = results.Count(static result => !result.Success);
        Console.WriteLine($"Completed {results.Count - failures}/{results.Count} trials successfully.");
        return new BenchmarkRunSummary(runRoot, failures);
    }

    private async Task<BenchmarkResult> RunCaseAsync(
        string runRoot,
        string torrentPath,
        BenchmarkCase benchmarkCase,
        string revision,
        CancellationToken cancellationToken)
    {
        string caseRoot = UniqueCaseDirectory(runRoot, benchmarkCase);
        string dataRoot = Path.Combine(caseRoot, "data");
        Directory.CreateDirectory(dataRoot);
        string targetLog = Path.Combine(caseRoot, "target.log");
        string testerLog = Path.Combine(caseRoot, "tester.log");

        int port = ReservePort();

        if (benchmarkCase.Mode == "metadata")
        {
            return await RunMetadataCaseAsync(
                torrentPath, benchmarkCase, revision, caseRoot, targetLog, testerLog, port, cancellationToken)
                .ConfigureAwait(false);
        }

        int testerExitCode = -1;
        int? targetExitCode = null;
        var metrics = new ProcessMetrics(0, 0, 0, 0, 0, 0);
        var elapsed = Stopwatch.StartNew();
        string? error = null;
        double downloadRate = 0;
        double uploadRate = 0;

        try
        {
            string targetDataRoot = await PrepareDataAsync(
                benchmarkCase, torrentPath, dataRoot, caseRoot, cancellationToken).ConfigureAwait(false);

            ProcessStartInfo targetStart = CreateTargetStartInfo(benchmarkCase, torrentPath, targetDataRoot, caseRoot, port);
            string? readyMarker = benchmarkCase.Engine == "peersharp" ? "Ready           :" : null;
            await using CapturedProcess target = CapturedProcess.Start(targetStart, targetLog, readyMarker);
            if (readyMarker is not null)
            {
                await target.WaitUntilReadyAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (target.HasExited) throw new InvalidOperationException($"Target exited during startup with code {target.ExitCode}.");
            }

            ProcessStartInfo testerStart = CreateTesterStartInfo(benchmarkCase, torrentPath, port);
            await using CapturedProcess tester = CapturedProcess.Start(testerStart, testerLog);
            var sampler = new ProcessSampler(target.Process);
            sampler.Start();
            elapsed.Restart();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                while (!tester.HasExited)
                {
                    sampler.Sample();
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
                }

                await tester.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = $"Transfer exceeded the {options.TimeoutSeconds}s timeout.";
            }

            elapsed.Stop();
            testerExitCode = tester.ExitCode ?? -1;
            metrics = sampler.Finish(elapsed.Elapsed.TotalSeconds);

            TesterSummary testerSummary = ParseTesterSummary(testerLog);
            downloadRate = testerSummary.DownloadRate;
            uploadRate = testerSummary.UploadRate;
            if (error is null && testerExitCode != 0) error = $"connection_tester exited with code {testerExitCode}.";
            if (error is null && !testerSummary.HasRates) error = "connection_tester did not report a transfer rate.";
            if (error is null && !TesterCompletedTransfer(benchmarkCase.Mode, testerSummary))
            {
                error = benchmarkCase.Mode switch
                {
                    "download" => $"connection_tester sent only {testerSummary.SentPercent:0.0}% of the payload.",
                    "upload" => $"connection_tester received only {testerSummary.ReceivedPercent:0.0}% of the payload.",
                    _ => $"connection_tester transferred only sent={testerSummary.SentPercent:0.0}%, " +
                        $"received={testerSummary.ReceivedPercent:0.0}%."
                };
            }

            if (error is null && benchmarkCase.Mode is "download" or "dual")
            {
                Func<bool> targetCompleted = benchmarkCase.Engine == "peersharp"
                    ? () => LogContains(targetLog, "All complete")
                    : () => HasResumeFile(targetDataRoot);
                bool completed = await WaitForSignalAsync(
                    targetCompleted,
                    TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 30)),
                    sampler,
                    target,
                    cancellationToken).ConfigureAwait(false);
                if (!completed)
                {
                    error = $"{benchmarkCase.Engine} never reported the downloaded torrent complete.";
                }
            }

            await target.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            targetExitCode = target.ExitCode;

            // Only an engine that ended by itself has an exit code worth reading. client_test takes
            // its keystrokes from the console rather than standard input, so in every mode that does
            // not hand it a self-exit flag the harness has to terminate it - and failing the trial on
            // the code that produces would discard libtorrent's upload and dual runs while keeping
            // PeerSharp's, which honours --control-stdin. A benchmark that disqualifies only the
            // other engine is worse than one that checks nothing.
            if (error is null && !target.WasKilled && targetExitCode is not 0)
            {
                error = $"Target exited with code {targetExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            elapsed.Stop();
            error = ex.Message;
        }
        finally
        {
            if (!options.KeepRunData)
            {
                TryDeleteRunData(dataRoot, runRoot);
            }
        }

        return new BenchmarkResult
        {
            Engine = benchmarkCase.Engine,
            EngineRevision = revision,
            Mode = benchmarkCase.Mode,
            Variant = benchmarkCase.Variant,
            Backend = benchmarkCase.Backend,
            Iteration = benchmarkCase.Iteration,
            Warmup = benchmarkCase.Warmup,
            SizeMiB = options.SizeMiB,
            FileCount = options.FileCount,
            PeerCount = options.PeerCount,
            DurationSeconds = elapsed.Elapsed.TotalSeconds,
            DownloadMBps = downloadRate,
            UploadMBps = uploadRate,
            CpuSeconds = metrics.CpuSeconds,
            CpuPercentOneCore = metrics.CpuPercentOneCore,
            PeakWorkingSetBytes = metrics.PeakWorkingSetBytes,
            PeakPrivateBytes = metrics.PeakPrivateBytes,
            ReadBytes = metrics.ReadBytes,
            WriteBytes = metrics.WriteBytes,
            TesterExitCode = testerExitCode,
            TargetExitCode = targetExitCode,
            Success = error is null,
            Error = error,
            ArtifactDirectory = Path.GetRelativePath(runRoot, caseRoot).Replace('\\', '/')
        };
    }

    private async Task<Dictionary<string, string>> CreateFixturesAsync(string runRoot, CancellationToken cancellationToken)
    {
        string fixtureRoot = Path.Combine(runRoot, "fixtures");
        Directory.CreateDirectory(fixtureRoot);
        var fixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string variant in options.Variants)
        {
            string path = Path.Combine(fixtureRoot, $"fixture-{variant}-{options.SizeMiB}m.torrent");
            string version = variant switch { "v1" => "1", "v2" => "2", _ => "h" };
            ProcessOutput output = await ProcessUtility.RunAsync(
                tools.ConnectionTester,
                ["gen-torrent", "-s", options.SizeMiB.ToString(CultureInfo.InvariantCulture),
                    "-n", options.FileCount.ToString(CultureInfo.InvariantCulture), "-V", version, "-t", path],
                fixtureRoot,
                cancellationToken,
                progress: Console.Out).ConfigureAwait(false);
            if (output.ExitCode != 0) throw new InvalidOperationException($"Fixture generation failed.\n{output.Combined}");
            fixtures.Add(variant, path);
        }

        return fixtures;
    }

    private async Task<string> PrepareDataAsync(
        BenchmarkCase benchmarkCase,
        string torrentPath,
        string dataRoot,
        string caseRoot,
        CancellationToken cancellationToken)
    {
        if (benchmarkCase.Mode != "upload") return dataRoot;

        var arguments = new List<string> { "gen-data", "-t", torrentPath, "-P", dataRoot };
        if (benchmarkCase.Engine == "libtorrent") arguments.Add("-R");
        ProcessOutput generated = await ProcessUtility.RunAsync(
            tools.ConnectionTester, arguments, caseRoot, cancellationToken).ConfigureAwait(false);
        if (generated.ExitCode != 0) throw new InvalidOperationException($"Seed data generation failed.\n{generated.Combined}");

        if (benchmarkCase.Engine == "peersharp")
        {
            // connection_tester's generator creates <save-path>/<torrent-name>/<files>.
            // PeerSharp maps this synthetic torrent's leaf paths relative to --out, so its
            // effective save path is the generated torrent directory itself.
            string peerSharpDataRoot = Path.Combine(dataRoot, Path.GetFileNameWithoutExtension(torrentPath));
            string resumeRoot = Path.Combine(peerSharpDataRoot, ".peersharp-resume");
            using var preparationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preparationTimeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            ProcessOutput checkedData = await ProcessUtility.RunAsync(
                tools.Dotnet,
                [tools.PeerSharpCli, torrentPath, "--out", peerSharpDataRoot, "--recheck", "--no-dht", "--port", "0",
                    "--resume", resumeRoot, "--interval", "3600"],
                caseRoot,
                preparationTimeout.Token).ConfigureAwait(false);
            if (checkedData.ExitCode != 0)
            {
                throw new InvalidOperationException($"PeerSharp seed preparation failed.\n{checkedData.Combined}");
            }

            return peerSharpDataRoot;
        }

        return dataRoot;
    }

    private ProcessStartInfo CreateTargetStartInfo(
        BenchmarkCase benchmarkCase,
        string torrentPath,
        string dataRoot,
        string caseRoot,
        int port)
    {
        if (benchmarkCase.Engine == "peersharp")
        {
            var arguments = new List<string>
            {
                tools.PeerSharpCli, torrentPath, "--out", dataRoot, "--no-dht", "--port",
                port.ToString(CultureInfo.InvariantCulture), "--interval", "3600", "--control-stdin"
            };
            if (benchmarkCase.Mode is "upload" or "dual") arguments.Add("--seed");
            if (benchmarkCase.Mode == "upload")
            {
                arguments.AddRange(["--resume", Path.Combine(dataRoot, ".peersharp-resume")]);
            }

            return ProcessUtility.CreateStartInfo(tools.Dotnet, arguments, caseRoot, redirectInput: true);
        }

        string counters = Path.Combine(caseRoot, "libtorrent-counters.log");
        string events = Path.Combine(caseRoot, "libtorrent-events.log");
        var clientArguments = new List<string>
        {
            torrentPath,
            "-k",
            "-O", counters,
            "-T", (options.PeerCount * 2).ToString(CultureInfo.InvariantCulture),
            "-f", events,
            "-s", dataRoot,
            $"--listen_interfaces=127.0.0.1:{port}",
            "--enable_dht=0",
            "--enable_lsd=0",
            "--enable_upnp=0",
            "--enable_natpmp=0",
            "--allow_multiple_connections_per_ip=1",
            $"--connections_limit={options.PeerCount * 2}",
            "--alert_mask=error,status,connect,performance_warning,storage,peer",
            "-i", benchmarkCase.Backend
        };
        // In dual mode the target must stay alive after its download completes so it can finish
        // uploading those pieces to the tester's leecher connections.
        if (benchmarkCase.Mode == "download") clientArguments.Add("-1");
        if (benchmarkCase.Mode == "upload") clientArguments.AddRange(["-e", "240"]);
        return ProcessUtility.CreateStartInfo(tools.ClientTest, clientArguments, caseRoot, redirectInput: true);
    }

    /// <summary>
    /// Times a BEP 9 metadata fetch: how long an engine takes to turn a magnet link into a torrent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The peer here is libtorrent's <c>client_test</c> rather than <c>connection_tester</c>, which
    /// has no BEP 9 at all - it speaks the base wire protocol and the v2 hash requests and nothing
    /// else. So the shared counterpart is a real libtorrent session holding the .torrent, and each
    /// engine in turn joins by magnet against that same session. It serves the metadata out of the
    /// file, so no payload is generated or transferred.
    /// </para>
    /// <para>
    /// Both engines are timed the same way and by this process: from starting the child to seeing it
    /// announce, in its own log, that the metadata arrived. That includes each runtime's startup,
    /// which is a real difference between them and a large fraction of a fetch this short - the
    /// summary says so rather than netting it out, because subtracting an estimate would be a worse
    /// answer than reporting an honest total.
    /// </para>
    /// </remarks>
    private async Task<BenchmarkResult> RunMetadataCaseAsync(
        string torrentPath,
        BenchmarkCase benchmarkCase,
        string revision,
        string caseRoot,
        string targetLog,
        string seedLog,
        int port,
        CancellationToken cancellationToken)
    {
        string dataRoot = Path.Combine(caseRoot, "data");
        Directory.CreateDirectory(dataRoot);

        var elapsed = new Stopwatch();
        var metrics = new ProcessMetrics(0, 0, 0, 0, 0, 0);
        string? error = null;
        int? targetExitCode = null;
        long metadataBytes = new FileInfo(torrentPath).Length;

        try
        {
            string exactTopic = TorrentInfoHash.ReadExactTopic(
                torrentPath, v2Only: benchmarkCase.Variant == "v2");
            // The peer travels in the magnet (BEP 9's x.pe) rather than as an engine-specific flag,
            // so both engines are handed the identical link and neither gets a different route in.
            string magnet = $"magnet:?xt={exactTopic}&x.pe=127.0.0.1:{port}";

            await using CapturedProcess seed = CapturedProcess.Start(
                CreateMetadataSeedStartInfo(torrentPath, caseRoot, port), seedLog);

            // No ready marker to wait on: client_test draws a screen rather than announcing itself.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (seed.HasExited)
            {
                throw new InvalidOperationException($"Metadata seed exited during startup with code {seed.ExitCode}.");
            }

            ProcessStartInfo targetStart = CreateMetadataTargetStartInfo(benchmarkCase, magnet, dataRoot, caseRoot, port);
            elapsed.Restart();
            await using CapturedProcess target = CapturedProcess.Start(targetStart, targetLog);
            var sampler = new ProcessSampler(target.Process);
            sampler.Start();

            // Each engine announces the arrival in the only way it offers live. PeerSharp prints it
            // and exits. client_test's alert log is block-buffered and reaches disk when the process
            // ends, which is no use while it is running - but it saves resume data the moment the
            // metadata lands, and that file appearing is the same event.
            Func<bool> arrivedSignal = benchmarkCase.Engine == "peersharp"
                ? () => LogContains(targetLog, "Metadata in")
                : () => HasResumeFile(dataRoot);

            bool arrived = await WaitForSignalAsync(
                arrivedSignal, TimeSpan.FromSeconds(options.TimeoutSeconds), sampler, target, cancellationToken)
                .ConfigureAwait(false);

            elapsed.Stop();
            if (!arrived)
            {
                error = $"Metadata did not arrive within the {options.TimeoutSeconds}s timeout.";
            }

            metrics = sampler.Finish(elapsed.Elapsed.TotalSeconds);
            await target.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            targetExitCode = target.ExitCode;
            await seed.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            elapsed.Stop();
            error = ex.Message;
        }

        return new BenchmarkResult
        {
            Engine = benchmarkCase.Engine,
            EngineRevision = revision,
            Mode = benchmarkCase.Mode,
            Variant = benchmarkCase.Variant,
            Backend = benchmarkCase.Backend,
            Iteration = benchmarkCase.Iteration,
            Warmup = benchmarkCase.Warmup,
            SizeMiB = options.SizeMiB,
            FileCount = options.FileCount,
            PeerCount = 1,
            DurationSeconds = elapsed.Elapsed.TotalSeconds,
            MetadataBytes = metadataBytes,
            DownloadMBps = 0,
            UploadMBps = 0,
            CpuSeconds = metrics.CpuSeconds,
            CpuPercentOneCore = metrics.CpuPercentOneCore,
            PeakWorkingSetBytes = metrics.PeakWorkingSetBytes,
            PeakPrivateBytes = metrics.PeakPrivateBytes,
            ReadBytes = metrics.ReadBytes,
            WriteBytes = metrics.WriteBytes,
            TesterExitCode = 0,
            TargetExitCode = targetExitCode,
            Success = error is null,
            Error = error,
            ArtifactDirectory = caseRoot
        };
    }

    /// <summary>
    /// Waits for the engine to say the metadata arrived, sampling the process meanwhile.
    /// </summary>
    private static async Task<bool> WaitForSignalAsync(
        Func<bool> arrived,
        TimeSpan timeout,
        ProcessSampler sampler,
        CapturedProcess target,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            sampler.Sample();

            if (arrived())
            {
                return true;
            }

            if (target.HasExited)
            {
                // PeerSharp exits once it has the metadata, so one last look decides it.
                return arrived();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Reads a log both children still hold open for writing.</summary>
    private static bool LogContains(string logPath, string marker)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(marker, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // A partially written line. The next poll is 10ms away.
            return false;
        }
    }

    private ProcessStartInfo CreateMetadataSeedStartInfo(string torrentPath, string caseRoot, int port)
    {
        var arguments = new List<string>
        {
            torrentPath,
            "-k",
            "-s", Path.Combine(caseRoot, "seed"),
            "-f", Path.Combine(caseRoot, "seed-events.log"),
            $"--listen_interfaces=127.0.0.1:{port}",
            "--enable_dht=0",
            "--enable_lsd=0",
            "--enable_upnp=0",
            "--enable_natpmp=0",
            "--alert_mask=error,status,connect,peer"
        };

        return ProcessUtility.CreateStartInfo(tools.ClientTest, arguments, caseRoot, redirectInput: true);
    }

    private ProcessStartInfo CreateMetadataTargetStartInfo(
        BenchmarkCase benchmarkCase, string magnet, string dataRoot, string caseRoot, int seedPort)
    {
        if (benchmarkCase.Engine == "peersharp")
        {
            var arguments = new List<string>
            {
                tools.PeerSharpCli, magnet, "--out", dataRoot, "--no-dht", "--port", "0",
                "--metadata-only", "--interval", "3600", "--control-stdin"
            };

            return ProcessUtility.CreateStartInfo(tools.Dotnet, arguments, caseRoot, redirectInput: true);
        }

        var clientArguments = new List<string>
        {
            magnet,
            "-k",
            "-s", dataRoot,
            "-f", Path.Combine(caseRoot, "leech-events.log"),
            "--listen_interfaces=127.0.0.1:0",
            "--enable_dht=0",
            "--enable_lsd=0",
            "--enable_upnp=0",
            "--enable_natpmp=0",
            "--alert_mask=error,status,connect,peer"
        };

        return ProcessUtility.CreateStartInfo(tools.ClientTest, clientArguments, caseRoot, redirectInput: true);
    }

    private ProcessStartInfo CreateTesterStartInfo(BenchmarkCase benchmarkCase, string torrentPath, int port)
    {
        string action = benchmarkCase.Mode switch
        {
            "download" => "upload",
            "upload" => "download",
            _ => "dual"
        };
        return ProcessUtility.CreateStartInfo(
            tools.ConnectionTester,
            [action, "-c", options.PeerCount.ToString(CultureInfo.InvariantCulture), "-d", "127.0.0.1", "-p",
                port.ToString(CultureInfo.InvariantCulture), "-t", torrentPath],
            Path.GetDirectoryName(torrentPath)!,
            redirectInput: false);
    }

    private List<BenchmarkCase> CreateCases()
    {
        var groups = new List<(string Engine, string Mode, string Variant, string Backend)>();
        foreach (string engine in options.Engines)
        {
            foreach (string mode in options.Modes)
            {
                foreach (string variant in options.Variants)
                {
                    IReadOnlyList<string> backends = engine == "libtorrent" ? options.LibtorrentBackends : ["managed"];
                    groups.AddRange(backends.Select(backend => (engine, mode, variant, backend)));
                }
            }
        }

        var random = new Random(options.RandomSeed);
        groups = groups.OrderBy(_ => random.Next()).ToList();
        var cases = new List<BenchmarkCase>();
        foreach ((string engine, string mode, string variant, string backend) in groups)
        {
            for (int i = 1; i <= options.Warmups; i++) cases.Add(new BenchmarkCase(engine, mode, variant, backend, i, Warmup: true));
            for (int i = 1; i <= options.Iterations; i++) cases.Add(new BenchmarkCase(engine, mode, variant, backend, i, Warmup: false));
        }

        return cases;
    }

    private static TesterSummary ParseTesterSummary(string testerLog)
    {
        if (!File.Exists(testerLog)) return default;
        using var stream = new FileStream(testerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        Match rates = RateRegex().Match(text);
        Match completion = CompletionRegex().Match(text);
        return new TesterSummary(
            rates.Success ? double.Parse(rates.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            rates.Success ? double.Parse(rates.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            completion.Success ? double.Parse(completion.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            completion.Success ? double.Parse(completion.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            rates.Success,
            completion.Success);
    }

    private static bool TesterCompletedTransfer(string mode, TesterSummary summary)
    {
        if (!summary.HasCompletion)
        {
            return false;
        }

        // The tester's own percentages are not exact: across recorded runs they overshoot routinely
        // (102.5% sent, and 199%, 200% and 300% received, which count redundant transfer) and a
        // finished transfer has been seen reported as 99.6%. A 99.9% floor therefore fails complete
        // runs at the boundary, which is how a dual trial failed here with sent=100.2%, received=99.6%.
        // A genuinely truncated transfer looks nothing like this - the observed ones report 47.3% and
        // 0.0% - so the margin costs no real detection.
        const double CompletePercent = 99.0;
        return mode switch
        {
            "download" => summary.SentPercent >= CompletePercent,
            "upload" => summary.ReceivedPercent >= CompletePercent,
            "dual" => summary.SentPercent >= CompletePercent && summary.ReceivedPercent >= CompletePercent,
            _ => false
        };
    }

    private static bool HasResumeFile(string dataRoot)
    {
        string resumeRoot = Path.Combine(dataRoot, ".resume");
        return Directory.Exists(resumeRoot) && Directory.EnumerateFiles(resumeRoot, "*.resume").Any();
    }

    private static async Task<string> GitRevisionAsync(string repository, CancellationToken cancellationToken)
    {
        ProcessOutput revision = await ProcessUtility.RunAsync(
            "git", ["-C", repository, "rev-parse", "HEAD"], repository, cancellationToken).ConfigureAwait(false);
        if (revision.ExitCode != 0) return "unknown";

        ProcessOutput status = await ProcessUtility.RunAsync(
            "git", ["-C", repository, "status", "--porcelain"], repository, cancellationToken).ConfigureAwait(false);
        return revision.StandardOutput.Trim() + (status.ExitCode == 0 && !string.IsNullOrWhiteSpace(status.StandardOutput) ? "+dirty" : string.Empty);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string UniqueCaseDirectory(string runRoot, BenchmarkCase benchmarkCase)
    {
        string basePath = Path.Combine(runRoot, "trials", benchmarkCase.Name);
        string path = basePath;
        int suffix = 2;
        while (Directory.Exists(path)) path = basePath + '-' + suffix++;
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteRunData(string dataRoot, string runRoot)
    {
        try
        {
            string resolvedData = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolvedRun = Path.GetFullPath(runRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolvedData.StartsWith(resolvedRun, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double scaled = value;
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return $"{scaled:0.0} {units[unit]}";
    }

    [GeneratedRegex(@"rate sent:\s*([\d.]+)\s*MB/s\s+received:\s*([\d.]+)\s*MB/s", RegexOptions.CultureInvariant)]
    private static partial Regex RateRegex();

    [GeneratedRegex(@"total sent:\s*([\d.]+)\s*%\s*received:\s*([\d.]+)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex CompletionRegex();

    private readonly record struct TesterSummary(
        double DownloadRate,
        double UploadRate,
        double SentPercent,
        double ReceivedPercent,
        bool HasRates,
        bool HasCompletion);
}
