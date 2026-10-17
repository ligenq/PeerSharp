# Transfer allocation profiling — 2026-10-17

Peerfluence was not running. These measurements use isolated local transfers rather than the
user's session, and measure both the seed and downloader in one process. Existing resume-memory
fixes were present in both versions.

## Findings and changes

1. `BlockCache.PrefetchAsync` rented contiguous read-ahead buffers from a pool whose maximum
   retained array was 16 KiB. The default four-block window needs 64 KiB, so every prefetch
   allocated another array. It now uses a separate pool sized up to the existing 64-block limit,
   retaining at most two arrays per bucket. Its retained windows are bounded to less than 4 MiB
   across the process, even if every supported window size is used. Buffer ownership, cancellation,
   cache invalidation, and read/write generation checks are unchanged.
2. Each requested block created a default `ConcurrentDictionary` for its peers, with lock and
   bucket capacity based on processor count. Usually it contains one peer. Its initial capacity
   is now one, with one lock stripe; duplicate requests can still grow it. Per-peer collections
   also use one stripe, reducing the work in frequent `Count` and snapshot operations. The tracker
   already serializes mutations; concurrent dictionaries, readers, and snapshot semantics remain.

A sampled allocation trace of six 128 MiB transfers attributed approximately 767 MiB to the nearest
`PrefetchAsync` frame before the change, versus 2.5 MiB afterward. The block-map factory frame fell
from about 37.6 to 6.9 MiB. These allocation-tick weights are estimates, include warmup and fixture
activity, and are diagnostic evidence rather than exact allocation accounting.

The remaining trace includes UDP receive/async machinery and request-age snapshots. Changing their
ownership or concurrency contracts would need a separate investigation. The UI refresh code was
inspected, but no UI timing claim is made without a running client profile.

## Verified transfers

Windows 11, Intel Core Ultra 9 285K (24 cores), .NET SDK 10.0.402 / runtime 10.0.13, Release,
workstation GC. Four separate processes ran in order: before, after, before, after. Each process
performed a full-size warmup and three measured 256 MiB transfers per transport. Transport order
alternated. No builds, tests, or other benchmarks ran alongside these measurements.

Every transfer completed and passed SHA-256 verification. Six measured samples per cell:

| Metric per 256 MiB transfer | TCP before | TCP after | uTP before | uTP after |
|---|---:|---:|---:|---:|
| Mean allocated MiB | 378.74 | 137.87 | 967.81 | 696.35 |
| Allocation reduction | | 63.6% | | 28.1% |
| Median elapsed seconds | 0.329 | 0.297 | 3.063 | 2.168 |
| Elapsed range, seconds | 0.318–1.318 | 0.274–0.325 | 2.157–3.317 | 2.155–2.230 |
| Mean process CPU seconds | 2.047 | 1.935 | 14.792 | 14.229 |
| Gen 2 collections | 14–15 | 0 | 14 | 0–1 |

Allocations are bytes allocated over the transfer, **not live heap size or Task Manager memory**.
The harness forces collection before timing, excludes seed recheck and final SHA-256 validation,
and polls completion every 10 ms. Local throughput, especially the before TCP outliers, varies.
These results support lower GC pressure and faster local transfers; they do not predict Internet
download rates, encrypted swarms, multi-peer scaling, or the client's total working set.
The largest reduction is in the seed's upload/read-ahead path. Smaller request maps benefit the
downloader; the combined process totals should not be interpreted as downloader-only savings.

## Cache benchmark confirmation

A separate BenchmarkDotNet **DefaultJob**, after the regression suites finished, used the final
cache fixture restricted to a 1 MiB cache and the default four-block read-ahead. Separate scratch
projects referenced the preserved before/after engine DLLs directly, so generated benchmark builds
could not accidentally rebuild both cases against the updated source. Their DLL hashes matched
the transfer runs.

| Sequential cache reads with synchronous fake storage | Before | After |
|---|---:|---:|
| Mean time per operation | 849.6 ns | 552.1 ns |
| 99.9% confidence interval | 845.8–853.5 ns | 550.4–553.8 ns |
| Allocated bytes per operation | 12.93 KiB | 134 B |

This is 35.0% less time and about 99% fewer allocated bytes in this cache-only workload. It
includes cache misses and subsequent hits in each prefetched window, with no physical disk latency.
The initial ShortJob runs across all cache/read-ahead sizes were exploratory; these normal-job
results are the speed confirmation.

## Reproduction

Preserve separate Release output directories before and after editing the engine. Run the copies
without tracing for timing/allocation comparisons:

```powershell
dotnet <copy>/PeerSharp.Benchmarks.dll --loopback 256 3 both
```

Collect separate diagnostic traces:

```powershell
dotnet-trace collect --profile dotnet-sampled-thread-time,gc-verbose --buffersize 64 `
  --output transfer.nettrace --show-child-io -- `
  dotnet <copy>/PeerSharp.Benchmarks.dll --loopback 128 2 both
```

The `BlockCacheBenchmarks` suite now includes disabled, default four-block, and maximum 64-block
read-ahead. Its backing storage completes synchronously, separating cache overhead from disk I/O.
Run with normal BenchmarkDotNet settings for a speed comparison; short runs serve as exploration.

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- `
  --filter '*BlockCacheBenchmarks.ReadMiss*'
```

The original saved engine DLL SHA-256 is
`D703D2F5A98E322D97B86EF023DE3CE1709F094832CA561DF1400B73C2E3C94F`;
the measured updated DLL is
`6A3299499DAA32C29812D61E0C03AB15B085B546F8DA77F0E7A35C587FA58FBE`.
Local traces, raw JSON samples, benchmark output, and test logs are in
`C:\temp\peerfluence-profiling`. Generated payload files remain in its `artifacts` directory
and `C:\repos\PeerSharp\artifacts\transport-loopback\cb8e1c80a8194048996efc273fab594b`;
automatic approval rejected their cleanup as blocked by policy. JSON results and traces are retained.

## Regression coverage

The new allocation test repeatedly fills and clears four-block and 64-block read-ahead windows.
It fails against the old code (8.49 MB and 137.35 MB allocated for 128 windows), checks a generous
6 MiB budget after warmup, and verifies every prefetched block against known bytes without extra
storage reads. It runs in a nonparallel collection because the bounded pools are process-wide.
Existing tests cover stale writes, cache clearing, endgame duplicates, stale request removal,
concurrent additions/removals, and growth to 32 peers requesting the same block.

Validation: Release engine build and Debug Peerfluence solution build succeeded with zero warnings.
The engine suite passed 3,931 tests (38 optional tests skipped); Peerfluence passed 904 unit tests
(four live tests skipped) and 266 headless tests against the local engine project. Project/package
selection was checked for all three direct consumers, and the app's copied engine DLL matched the
local Debug build by SHA-256.
