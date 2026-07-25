# PeerSharp Benchmarks

BenchmarkDotNet suites covering the engine's hot paths. These exist so performance claims can be
checked rather than assumed — the suite was added after a parallelisation change shipped that
turned out to be slower on the most-executed path in the engine.

## Running

Benchmarks must run in Release; BenchmarkDotNet refuses a Debug build.

```bash
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- --filter '*'
```

Pick a single suite:

```bash
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- --filter '*Storage*'
```

Iterate quickly while tuning (fewer iterations, wider error bars — never quote these numbers):

```bash
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- --filter '*Storage*' --job Short
```

Validate that the suites still build and their fixtures still work, without measuring anything:

```bash
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- --filter '*' --job Dry
```

## Suites

| Suite | Covers | Why it matters |
|---|---|---|
| `StorageBenchmarks` | 16 KiB block read/write, single-file and spanning two files | Called once per block by `BlockCache` — the hottest disk path in the engine |
| `PeerProtocolBenchmarks` | Encode/decode of Have, Request and Piece messages | Runs at the same rate as Storage on a saturated swarm |
| `BencodeBenchmarks` | Parse/write of torrents and tracker responses | Every tracker response and extension message; backs the "zero-copy" claim |
| `MerkleTreeBenchmarks` | Leaves, root, piece layer, piece verification | BEP 52 hashing for v2/hybrid torrents |
| `PiecePickerBenchmarks` | `PickNextPiece` across strategies and piece counts | Once per request slot per peer; scales with piece count |
| `TorrentFileBuilderBenchmarks` | V1/V2/Hybrid torrent creation | The clearest CPU-bound, user-visible operation |

## Comparing a change

There is no committed baseline file — machine-to-machine numbers are not comparable, and a stale
baseline is worse than none. To evaluate a change, measure both sides on the same machine in the
same session:

1. Run the relevant suite on `main`, save the output.
2. Apply the change, rebuild, run the same filter.
3. Compare **Mean and Allocated together**. An allocation regression on a path that runs thousands
   of times a second matters even when the mean looks flat, because the cost lands later as GC
   pressure rather than in the benchmark window.

Note that `Error` in a `--job Short` run is often larger than the difference being measured. Use
the default job before drawing a conclusion.

## Interpreting the Storage results

The single-file and cross-file cases pull in opposite directions and should be read together.
Blocks that straddle a file boundary can genuinely benefit from overlapping two file handles, but
they are a small minority of blocks — and rarer still in modern torrents, where BEP 47 padding
files align files to piece boundaries. A change that wins on the cross-file row while losing on
the single-file row is usually a net loss in production.
