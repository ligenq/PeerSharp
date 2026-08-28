# End-to-end measurements against libtorrent

This harness compares the two engines against the same deterministic peer implementation. It builds
the exact libtorrent Git checkout, generates one shared torrent and payload, starts one engine at a
time, and drives both with libtorrent's `connection_tester` over loopback.

It is deliberately separate from BenchmarkDotNet. Microbenchmarks answer whether one method got
faster; this harness measures the behavior a client experiences: transfer rate, engine CPU time,
working set, private bytes and disk I/O over a complete transfer.

## What the numbers in here are, and are not

They are measurements taken on one machine, over loopback, by a harness that is young and has already
been wrong more than once. Reading them as a verdict on either engine would be a mistake, and the
mistakes so far are the reason to say so rather than a hypothetical:

- A churn sweep was reported with its units inverted, because `connection_tester`'s own help calls a
  period a rate. The alarming figure that produced was a stress test two orders of magnitude past any
  real swarm, and the same run read the other way round at realistic settings.
- Dual mode's rate was measured over a window that included a connection-lifecycle bug, and still
  understates throughput by the health-check interval.
- A completion gate had to be added because trials were being recorded as measurements when the
  transfer had not finished, and then relaxed twice when it failed runs for reasons that were the
  tester's rather than either engine's.

Every one of those looked like a result before it was understood. BitTorrent has a lot of interacting
parts, and this harness keeps finding that its own numbers need a second look, so treat anything here
as a lead worth investigating rather than a claim worth repeating. Where a figure is quoted elsewhere
in the repository it is quoted with the conditions it was taken under, and it should stay that way.

## Why `connection_tester` is shared

Using PeerSharp as libtorrent's peer, then reversing the pair, would change both halves of the test.
`connection_tester` is instead the peer for every trial and reports the rates itself. Neither engine
gets to define its own transfer window or throughput calculation.

The harness disables DHT, LSD, UPnP and NAT-PMP and uses fixed loopback endpoints. Each trial gets an
isolated save directory. Case-group order is randomized from a recorded seed, while warmups always
precede measured iterations within their group.

## Why metadata mode uses `client_test` instead

`connection_tester` has no BEP 9 at all: it speaks the base wire protocol and the v2 hash requests
and nothing else. So `--modes metadata` swaps the shared counterpart for a real libtorrent session -
`client_test` holding the .torrent - and each engine in turn joins it by magnet. The peer address
travels in the magnet as `x.pe`, so neither side is configured differently from the other. Pure v2
torrents use their SHA-256 `btmh` exact topic; v1 and hybrid torrents use their SHA-1 `btih` identity.

The harness times both engines the same way and from outside: from starting the child process to the
child announcing in its own log that the metadata arrived. That interval includes the runtime's
startup, which is a real difference between them and a large share of a fetch this short. The summary
says so rather than netting it out; subtracting an estimate would be a worse answer than an honest
total. PeerSharp's CLI also self-times the fetch from inside the process, so `target.log` gives the
same number without .NET startup - about 0.11s lower, consistently, on the machine used here.

What arrives is the whole magnet-to-usable-torrent path, not just the wire transfer: the marker is
written once the metadata has been received, verified against the info hash and parsed into a torrent.
That is the operation a caller actually waits on, and it is why the number grows with the file count
rather than only with the number of metadata pieces.

Measured over loopback, 5 iterations after a warmup, medians:

| info dict | pieces | files | PeerSharp | libtorrent |
|---|---:|---:|---:|---:|
| 82 KiB | 6 | 1000 | 0.263 s | 1.044 s |
| 313 KiB | 20 | 4000 | 0.628 s | 1.051 s |

libtorrent is flat across both sizes because its number is not really the fetch: it dials from its
session tick, so roughly a second passes before the exchange starts and the exchange itself disappears
inside it. PeerSharp requests immediately and its time is visible work, which is the more useful of
the two shapes to have - a flat second tells you nothing about what to improve. Read the rows as
"PeerSharp finishes sooner", not as "PeerSharp's BEP 9 is four times faster".

## Commands

Run these from the PeerSharp repository root.

Inspect prerequisites without downloading or building anything:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- doctor
```

Build the PeerSharp CLI and the exact libtorrent checkout. The first build downloads Boost 1.88.0
into ignored `artifacts/` storage, verifies the archive's pinned SHA-256, initializes libtorrent's
pinned `deps/try_signal` submodule, and builds `client_test` plus `connection_tester`:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- build
```

A fast interoperability smoke test—not a publishable performance result:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- run `
  --skip-build --size-mib 32 --files 4 --peers 2 `
  --warmups 0 --iterations 1 --timeout 60
```

A useful v1 download/upload comparison:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- run `
  --skip-build --modes download,upload --variants v1 `
  --size-mib 256 --files 8 --peers 4 `
  --warmups 1 --iterations 5 --timeout 300
```

The wider protocol and disk-backend matrix:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- run `
  --skip-build --modes download,upload,dual --variants v1,v2,hybrid `
  --libtorrent-backends mmap,pread,posix `
  --size-mib 1024 --files 15 --peers 16 `
  --warmups 1 --iterations 5 --timeout 900
```

How long a magnet takes to become a torrent. `--files` sets the size of the info dictionary, and
`--peers` is ignored - the metadata comes from the one libtorrent session holding the torrent:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- run `
  --skip-build --modes metadata --variants v1 `
  --size-mib 2000 --files 1000 `
  --warmups 1 --iterations 5 --timeout 90
```

Use `--help` for every option. `PEERSHARP_LIBTORRENT_ROOT` changes the default sibling checkout,
or pass `--libtorrent-root` explicitly.

## Adverse conditions

Both of these are properties of the shared peer, so they apply to whichever engine is under test and
neither gets to choose its own version of the hostility.

`--churn <n>` makes the peer drop the connection and come back every `n` blocks of 16 KiB. It is a
period rather than a rate, so **a smaller number churns more**, and `connection_tester`'s own help
calls it "reconnects per second" where its code says `blocks_sent % churn == 0`. Two is severe: a
reconnect every thirty-two kilobytes, measured at roughly eleven hundred connections a second against
eight configured peers. Every other measurement here runs on connections made once and kept, which is
the one thing a real swarm never does, and connection setup and teardown is the part of the engine
this repository has found the most defects in:

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- run `
  --skip-build --modes download --variants v1 `
  --size-mib 256 --files 4 --peers 8 --churn 2 `
  --warmups 0 --iterations 5 --timeout 240
```

Churn is reliable at the gentle end and not in the middle. Measured over a 256 MiB download with
eight peers, reconnecting every 64, 16 and 4 MiB completes every trial; every 1 MiB failed seven of
twelve and every 0.25 MiB failed ten of twelve, on both engines alike. The cause is the tester rather
than either engine - its restart logic collapses, ending the run in a tenth of a second with all
eight connections dead and a fraction of the payload sent - and the completion gate catches those, so
they are discarded rather than reported as slow.

What the gate cannot repair is the trials that do survive at those levels: the rate is computed over
the tester's own lifetime, and a run that restarts connections has a shorter and less meaningful one.
libtorrent reads as 565 MB/s reconnecting every megabyte against 224 MB/s with no churn at all, which
is not a real speed-up. Treat churn throughput as sound at 256 blocks and above, and read anything
below that as a stress test whose rate means little - the CPU figures, which are sampled from the
engine process rather than derived from the tester's clock, stay usable further down.

`--corrupt` makes the peer send bad pieces sometimes. It only applies where the peer is the one
uploading - `download` and `dual` - because that is the direction the engine has to verify, and the
flag is left off elsewhere rather than recorded as a workload the run did not have.

Under corruption the tester's completion percentage stops meaning anything: an engine that bans the
peer sending bad data legitimately shows a low number, and one that re-requests shows more than a
hundred. So that check is skipped for those runs and the trial is judged on the engine reporting the
torrent complete, which is the only question corruption was asked to settle. Note that every peer is
corrupt here, unlike a real swarm - an engine that bans them all has nowhere left to go, and that is a
statement about this workload rather than about the engine.

## Results

Every run writes an independently usable directory under
`artifacts/peersharp-e2e/runs/<UTC timestamp>-<process id>/`:

- `manifest.json` records revisions, machine/runtime information, workload and cache policy.
- `results.json` is the complete structured result, including warmups and failed trials.
- `results.csv` is convenient for analysis in R, Python or a spreadsheet.
- `summary.md` reports medians from successful measured iterations only.
- `trials/<case>/target.log` and `tester.log` preserve both sides' output.
- libtorrent trials additionally retain session counters and alert output.

Failed trials are results. A timeout, rejected handshake, incomplete transfer, missing target
completion or unsupported variant remains visible in JSON/CSV and in the Markdown failure section;
it is never folded into a zero-valued median. The command exits nonzero if any trial fails. Reports
are rewritten after every trial, so a cancelled long matrix keeps all completed results.

## Reading the metrics

- Download/upload MB/s comes from `connection_tester`'s transfer window.
- CPU is the engine process only, sampled from immediately before the peer starts until the peer exits.
- `CPU % of one core` may exceed 100 when the engine uses several cores.
- Working set and private bytes are peak samples during the transfer window.
- Disk read/write byte counters use Windows process I/O counters. They are zero on platforms where
  the harness does not yet have a native counter provider.
- Warmups are retained for diagnosis but excluded from summary medians.
- `--peers N` is an upper bound rather than a count. `connection_tester` fills each connection's peer
  id with unseeded `rand()`, which is deterministic and per-thread, so some connections present an
  identical id - the same one, `2923be84...`, on every run. A peer id names a peer rather than a
  connection, so both engines correctly drop the duplicates: one is lost at `-c 4` and two at `-c 8`.
  This is the tester's own defect and is left alone, because patching the pinned checkout would cost
  more credibility than the connection is worth. In dual mode it lands on a seeder or a leecher at
  random, which is why the reported `received` percentage alternates between roughly 100% and 200%.

- Dual mode's rate includes the time PeerSharp takes to notice that its remaining peers are seeds. The
  transfer itself finishes in well under a second; the periodic health check that closes redundant
  connections runs every five seconds, and `connection_tester` measures until its last connection
  ends. So a dual figure here understates throughput by roughly that interval, and the mode is better
  read as a completeness check than as a rate.

Very small payloads mostly measure startup, scheduler ticks and process shutdown. Use at least a few
hundred MiB before comparing throughput, and use multiple measured iterations before comparing CPU or
memory.

## Cache and machine control

Windows does not provide an ordinary unprivileged per-file page-cache eviction API. The harness
therefore labels Windows results `warm/uncontrolled OS cache`; it does not pretend they are cold.
For cold-cache work, evict the files with an elevated external tool between trials or run on a system
where targeted cache eviction is available. Do not combine warm and cold results in one summary.

For low-noise measurements, close unrelated CPU/disk workloads, keep the power plan and CPU topology
fixed, and compare both engines in the same run. The recorded randomized group order reduces—but does
not eliminate—thermal and background-load bias.

## Build profile

The native build is Release, static, plaintext BitTorrent with WebTorrent, I2P and libtorrent compile-
time logging disabled. The measured session also disables network discovery. PeerSharp is published in
Release and runs with its sample CLI's workstation/concurrent GC configuration. These choices are
recorded here because changing them creates a different benchmark.

Encryption and uTP should be added as explicit future workload dimensions rather than silently mixed
into the baseline.

## qBittorrent

The installed qBittorrent application is intentionally not the primary target. It includes a GUI,
profile state, application scheduling and a separately built (and potentially different-revision)
libtorrent, all of which confound an engine comparison. It is useful later as a production-client
sanity check through an isolated temporary profile and Web API, but a qBittorrent result must be
labelled separately and must not be presented as the result for the exact checkout.
