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
| `BlockCacheBenchmarks` | Cache hit, miss, eviction, write-through | Sits above Storage, so its hit path runs at least as often |
| `PieceVerificationBenchmarks` | v1 SHA-1, v2 Merkle, BEP 30 Merkle | The longest CPU-bound operation a user waits on; a full recheck is nothing else |
| `ProtocolEncryptionBenchmarks` | RC4 raw, through the MSE lock, and 8 peers in parallel | Touches every byte on an encrypted connection |
| `PeerProtocolBenchmarks` | Encode/decode of Have, Request and Piece messages | Runs at the same rate as Storage on a saturated swarm |
| `BencodeBenchmarks` | Parse/write of torrents and tracker responses | Every tracker response and extension message; backs the "zero-copy" claim |
| `MerkleTreeBenchmarks` | Leaves, root, piece layer, piece verification | BEP 52 hashing for v2/hybrid torrents |
| `PiecePickerBenchmarks` | `PickNextPiece` across strategies and piece counts | Once per request slot per peer; scales with piece count |
| `AvailabilityBenchmarks` | `IncrementAvailability` / `GetAvailability`, single and whole-range | The data structure behind RarestFirst; driven by peer churn and `Have` traffic |
| `TorrentFileParseBenchmarks` | `TorrentFile.Parse` for v1/v2/hybrid, 1 and 500 files | Every torrent added, and every entry when a session is restored |
| `DhtRoutingTableBenchmarks` | `FindClosest`, `AddNode`, `GetAllNodes` at 500 and 8,000 nodes | Once per round of every iterative lookup; `AddNode` runs at inbound packet rate |
| `FileMapperBenchmarks` | `MapOffset` / `MapRange` from 2 to 10,000 files | Entered by every Storage read and write; scaling is hidden inside the Storage suite |
| `IpBlocklistBenchmarks` | Lookup hit/miss and 8-thread contention, 1k–200k ranges | Once per inbound connection and per discovered peer; connect storms arrive in parallel |
| `UtpBenchmarks` | 256-packet windows in order and reordered, SACK parsing | Per-packet path in the second-largest file in the library |
| `TorrentFileBuilderBenchmarks` | V1/V2/Hybrid torrent creation | The clearest CPU-bound, user-visible operation |

### Reading the concurrent benchmarks

`IpBlocklistBenchmarks.ContendedLookup` and `ProtocolEncryptionBenchmarks.ParallelPeersEncrypt`
report the cost of a whole **batch** of operations across threads, not single-operation latency.
Divide by the op count before comparing them to the single-threaded rows.

They also ask opposite questions, which is the point. The blocklist is one shared instance behind
one lock, so its concurrent row measures genuine contention. Each peer gets its own
`ProtocolEncryption`, with separate send and receive locks, so its concurrent row should show
near-linear scaling — it exists to catch the day that stops being true, not because contention is
expected.

`UtpBenchmarks` uses `[IterationSetup]` to rebuild the stream between iterations, because packet
processing advances sequence state and cannot be measured one call at a time. Its figures are per
256-packet window.

### One benchmark that is deliberately an upper bound

`AvailabilityBenchmarks.BulkIncrementUnbatched` walks the whole piece range through the per-piece
API, which takes the picker's selection lock once **per piece**. The method it stands in for,
`PiecePicker.RegisterPeerAvailability`, takes that lock once and loops inside it. The real cost of
a peer connecting is therefore lower than that row, by roughly the per-call locking — compare it
against `Whole range, bitfield probe only` to see how much of the walk is lock acquisition rather
than reading the peer's bitfield.

`RegisterPeerAvailability` takes a concrete `PeerCommunication`, which cannot be constructed
without a live torrent, bandwidth manager and transport, so the batched path is not directly
measurable here. Do not quote the unbatched row as the cost of a peer connect.

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
