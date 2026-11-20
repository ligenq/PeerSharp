# Testing PeerSharp

How to run the suites that need more than `dotnet test`, and how to read what they report.
The unit and integration tests need no setup and run on every build; everything below does not.

| Suite | Gate | Where |
|---|---|---|
| Unit, integration, architecture, property-based | none - runs on every build | `tests/PeerSharp.Tests` |
| Replayable fault scenarios | four fixed seeds in CI, 64 in the Release workflow | [Fault scenarios](#replayable-fault-scenarios) |
| Coverage-guided fuzzing | AFL++ and a corpus | [PeerSharp.Fuzz](PeerSharp.Fuzz/README.md) |
| Local counterpart-client interop | real client binaries; runs on demand and during release validation | `tests/PeerSharp.Tests/Interop` |
| Live DHT probes | `PEERSHARP_INTEROP=1` | `tests/PeerSharp.Tests/Interop` |
| Real-swarm soak | `PEERSHARP_SOAK=1` plus content you choose | `tests/PeerSharp.Tests/Interop` |
| Real-swarm streaming with longer and shorter request queues | `PEERSHARP_SOAK=1` plus content you choose; `PEERSHARP_STREAMING_QUEUES`, `_ROUNDS`, `_BITRATE`, `_SECONDS`, `_REPORT`; `_PLAYERS` (`strict`, `exoplayer`) the player profiles; `_FROM_START=1` plays from the file's start, as a film is watched, uncapped, and reports every stall | `tests/PeerSharp.Tests/Interop/RealSwarmStreamingTests.cs` |
| Streaming from a mixed local swarm: first byte and stalls, from the start and after a seek | `PEERSHARP_STREAMING=1`; `PEERSHARP_STREAMING_REPORT` names a file the numbers are appended to, `PEERSHARP_STREAMING_LOG` one the player's engine logs to in detail, `PEERSHARP_STREAMING_SWARM` the big-piece test's seeders, as `24x384` (two dozen at 384 KiB/s) or several such groups separated by commas | `tests/PeerSharp.Tests/Integration/StreamingSwarmTests.cs` |

## Replayable fault scenarios

The uTP test feeds reordered SYNs, dropped packets, retries and resets through the engine's
receive path, then advances the fake handshake clock or shuts down the engine. It checks the
per-address and global admission limits, both stream indexes and whether the pipe workers drain.
The resume test injects cancellation and I/O failures at file-handle barriers during writes and
session saves. It reopens the persisted state and hashes every piece it claims directly from disk.
It also checks that the missing pieces can be downloaded again.

The BEP 44 test varies reply order and injects lost, duplicate and wrong-endpoint replies.
It checks that publishing reaches the token holder and that cancellation drains pending queries.
The BitTorrent v2 test holds a piece while peers reject or abandon hash requests, then supplies an
invalid proof followed by a valid one. It checks the retained payload verifies and reaches disk.
The session publication test cancels a save on each side of the snapshot pointer swap and
reopens the session to check that metadata, options and resume data match a single generation.
The endgame test disconnects a peer, delivers a stale block and replaces a duplicate request
while cancellation is in flight. It checks that the final piece is published once and both
request indexes drain.
The encrypted transfer test drops queued bytes partway through a piece on a real loopback
connection. A fresh peer must finish the transfer while the verified piece stays available.
The test compares the completed file byte for byte with the original payload.
The storage move test forces the copy fallback for the first file, then interrupts the move
with cancellation or an I/O failure. It checks rollback, retries from the saved path, and
reopens and rehashes the pieces after the retry.
The storage move process test kills the engine after the first file moves and on either side of
publishing the new download path. The restarted session must recover every file at the root
selected by its snapshot and start with all pieces available. Another case kills the mover during
a partial copy, then kills the recovery process during a second copy. The final restart must remove
both temporary copies while preserving the existing destination file and an unrelated temporary file.
The HTTP range test cancels one of two overlapping reads on either side of piece verification.
It checks streaming priorities, the surviving response bytes, and shutdown with two pending reads.
The tracker restart test holds an HTTP announce past the stop deadline, then releases the old
success or failure during a new run. It checks peer discovery, swarm counts, retry timing and the
tracker ID sent with the next announce.
The web-seed test stalls or truncates the second range of a piece that spans two files. It checks
that a healthy source supplies both ranges, a duplicate arriving during publication is discarded,
and waiting readers get the verified bytes.
The magnet handoff test delivers authenticated metadata through the peer message handler, then
stops the torrent or pauses the session during the rebuild. It checks file selections, publication
and connection slots for public and private metadata.
The session crash test runs two fixed cases. It kills a writer process before or after the snapshot
pointer swap, then checks recovery and a subsequent save from a fresh process.

The lifecycle model runs generated commands against two torrents sharing one download queue slot.
It checks the registry, start/stop state, pause intent and queue eligibility after each command,
including after saving and reopening. Failures print the original history and a reduced history
that still reproduces the same invariant violation. The file-check schedule test covers all six
orders of a pending read completing, an explicit stop, and a session pause. It checks the final
state and then reopens the session to verify that resuming honors the stop.

The connection slot model keeps one incoming peer connected while a second torrent dials,
accepts peers, receives metadata, and stops or restarts. It compares the shared governor's
active and pending counts with the peers that own them and probes the ceiling with a fresh
incoming connection after each command. The history includes disconnects that finish before
and after shutdown, plus an incoming handshake that returns after its run has been stopped.

The block request model uses the real scheduler with two scripted peers and a two-block piece.
It varies timeout, choke, rejection, disconnect and late reply order. It checks both request
indexes and the outgoing requests after each command, then restores a healthy peer and verifies
the announced piece against the bytes on disk. It exercises base-protocol and Fast Extension chokes,
including a choke during request scheduling and a valid block already queued when a choke arrives.

Run all scenarios locally with the same four seeds used in pull requests.

```powershell
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Release -- --filter-trait Suite=ReplayableFaults
```

Run the 64-seed sweep with the same flags as the Release workflow.

```powershell
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Release -- --explicit on --filter-trait Suite=ReplayableFaults
```

Failures print a seed and event trace. To replay seed 63, for example, select its `FaultSeed` trait.

```powershell
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Release -- --explicit on --filter-trait FaultSeed=63
```

Add a class filter such as `--filter-class '*.DhtLookupFaultTests'` to replay one scenario.
Use `--filter-class '*.TorrentLifecycleModelTests'` with a seed to replay a lifecycle history.
Use `--filter-class '*.FileCheckScheduleTests'` to exercise all six fixed schedules.
Use `--filter-class '*.ConnectionSlotModelTests'` with a seed to replay a connection history.
Use `--filter-class '*.BlockRequestModelTests'` with a seed to replay a block request history.
The seed controls the inputs, fake clocks control protocol deadlines, and barriers control the
storage and session fault points. The .NET thread pool schedules the remaining continuations.

Release validation also runs the existing **Interop** and **Fuzz** workflows on the candidate
commit. Check the interop reports for which external clients ran and the fuzz artifacts for crashes.
When a review or a sweep exposes an invariant violation, keep a regression that fails on the affected
revision and passes with the fix.

## Mutation and concurrency testing

The manually triggered mutation workflow uses `stryker-config.json` and Stryker's MTP runner. Run the same
configuration locally after installing the tool version pinned in `.github/workflows/mutation.yml`:

```powershell
$env:StrykerRun = 'true'
try { dotnet stryker --test-runner mtp }
finally { Remove-Item Env:StrykerRun }
```

`StrykerRun` excludes the integration lane from mutation builds and relaxes nullable errors for
mutated code; ordinary builds keep both. Keep `coverage-analysis` set to `off` until coverage
attribution is verified with a known covered mutant. Otherwise covered code can be reported as
`NoCoverage`. Excluding more test lanes also removes their coverage, including the concurrency
tests for lifetime counters. The mutation score applies only to the units listed in the config.

`ConcurrencyStress` repeats scenarios on real threads; it is stress testing, not systematic
schedule exploration or proof of race freedom. When adding concurrency or property tests,
check that they detect a deliberately broken implementation before relying on a clean result.

The allocation checks in `ResumePersistenceMemoryTests` run in a child test process using the
current build. Their process-wide allocation counter must exclude runner activity, other test
assemblies and background work from earlier tests. Collection isolation alone cannot do that.
The parent verifies that the selected child test executed and passed, and reports its output.

## Real-Swarm Interop and Soak Testing

Local swarms prove the protocol encodes correctly; they cannot detect the failure that actually
decides production viability — being quietly throttled, choked or dropped by real libtorrent,
qBittorrent and Transmission peers. Those clients enforce their
own expectations, and a client they dislike still *appears* to work. It just downloads at a fraction
of the speed it should, for reasons nothing local will surface.

`tests/PeerSharp.Tests/Interop/RealSwarmSoakTests.cs` measures that against a live swarm:

| Test | What it answers |
|------|-----------------|
| `Interop_HowRealClientsTreatUs` | Which implementations we meet, and how many of each ever unchoke us, send us data, or want ours |
| `Soak_ConnectionsStayBoundedUnderChurn` | Whether the connection pool stays inside its ceiling as peers come and go over a long run |
| `Interop_DownloadRunsToCompletion` | Whether a download from strangers actually finishes, and at what rate |
| `Interop_MultipleTorrentsAtOnce` | Whether several live swarms in one engine starve each other, sharing bandwidth channels, the connection governor and one DHT node |
| `Seeding_HowRealClientsRequestFromUs` | The other direction — whether real clients request from us when we hold the data, and whether we actually deliver |

These are diagnostics, not pass/fail gates — swarm composition is not ours to control, so the numbers
are the deliverable and the assertions cover only what would make the numbers meaningless. They stay
opt-in: each requires `PEERSHARP_SOAK=1`, separately from the DHT probes' `PEERSHARP_INTEROP=1`,
because they transfer real data for a long time against a swarm of strangers.

The rest of `PeerSharp.Tests.Interop` — the local counterpart-client tests and the loopback ones —
runs through the Interop workflow (`.github/workflows/interop.yml`) on demand and during release
validation. It needs real client binaries and real transfers. Trigger it when validating changes to
protocol handling or transport behavior. Check the skip count to see whether tests found their external clients.

**You choose the content.** Nothing is hardcoded; the tests skip until you point them somewhere.
Use something you have the right to distribute. Projects that publish their own releases over
BitTorrent are the conventional choice, and are also the most useful to measure against — they are
well seeded by a broad mix of client implementations:

| Source | Where |
|--------|-------|
| Debian installer images | `https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/` |
| Ubuntu releases | `https://releases.ubuntu.com/` (each `.iso` has an `.iso.torrent`) |
| Tails | `https://tails.net/install/` |
| Internet Archive | most public-domain items offer a `.torrent` from their details page |

Both variables accept several entries separated by `;`, which is what `Interop_MultipleTorrentsAtOnce`
uses. Prefer a mix of sizes: swarm composition differs sharply between projects, so a conclusion drawn
from one torrent is really a conclusion about that torrent's seeders.

```bash
PEERSHARP_SOAK=1 \
PEERSHARP_SOAK_TORRENT=https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/debian-13.6.0-amd64-netinst.iso.torrent \
PEERSHARP_SOAK_SECONDS=600 \
PEERSHARP_SOAK_MAX_BYTES=1073741824 \
dotnet test tests/PeerSharp.Tests --filter FullyQualifiedName~RealSwarmSoakTests --logger "console;verbosity=detailed"
```

| Variable | Purpose |
|----------|---------|
| `PEERSHARP_SOAK` | Must be `1`; nothing runs otherwise |
| `PEERSHARP_SOAK_TORRENT` | `.torrent` path or http(s) URL |
| `PEERSHARP_SOAK_MAGNET` | Magnet link, as an alternative to the above |
| `PEERSHARP_SOAK_SECONDS` | Duration of the interop measurement (default 600) |
| `PEERSHARP_SOAK_CHURN_SECONDS` | Duration of the churn soak (default 1800) |
| `PEERSHARP_SOAK_COMPLETION_SECONDS` | Budget for the completion run (default 1800) |
| `PEERSHARP_SOAK_MAX_BYTES` | Hard ceiling on data pulled per run (default 1 GiB) |
| `PEERSHARP_SOAK_SEED_SECONDS` | Duration of the seeding run (default 900) |
| `PEERSHARP_SOAK_SEED_PATH` | Directory holding a complete copy of the torrent's content, for the seeding run |
| `PEERSHARP_SOAK_RATE_BYTES` | Rate cap applied globally and per torrent (default 2 MiB/s) |

### Reading the report

Read the **unchoke** column first. A client that meets fifty libtorrent peers and is unchoked by none
of them has an interop bug, however healthy the aggregate throughput looks. Two things confound that
reading and are called out in the output itself:

- **Tit-for-tat.** If the `we served` column is near zero everywhere, low unchoke rates say more
  about our upload than about anyone's opinion of us. A leech-only run cannot conclude much; re-run
  while seeding.
- **`seen once`** counts connections that did not outlive one sampling interval. A cluster of those
  against one implementation is what a post-handshake rejection looks like from the outside.
- **Seeds cannot want your data.** The upload columns are reported against the incomplete peers only,
  because on a distribution swarm most peers already have everything and counting them would
  manufacture a problem that is not there. Meeting incomplete peers and serving *none* of them is the
  finding worth chasing.

Compare runs against each other rather than against an absolute target.

### What is pinned locally

A bug worth finding on a real swarm is cheaper to catch deterministically, so the behaviour the soak
runs measure is held in place by local suites that run in CI. These assert on real bytes rather than
on mocked calls, which is the distinction that matters here: our own parser tolerates orderings and
timings that strict clients reject, so two PeerSharp instances can interoperate happily over a wire
format real clients would ignore.

- `RateLimitTests` — limits constrain throughput in both directions, per torrent and globally, in
  both encryption modes, with a control proving an unlimited transfer is genuinely faster so a broken
  transfer cannot masquerade as a working limiter.
- `ResumeIntegrityTests` — completed downloads match the source byte for byte, including multi-file
  layouts where pieces straddle file boundaries; a mid-transfer restart keeps its verified pieces;
  and a recheck detects corruption rather than always reporting success.
- `HandshakeMessageOrderTests` and `WireSequenceTests` — the opening sequence as a real socket sees
  it, captured and asserted on directly, since testing against ourselves cannot cover a message
  order only strangers are strict about.
- `MseConformanceTests` — our encrypted handshake decoded by an implementation that shares no code
  with the engine, written from the spec and cross-checked against Transmission's `peer-mse.cc`.
  Encryption is the production path, and self-consistent tests would agree just as happily on a wrong
  keystream.
- `EncryptedUtpTests` and `EncryptionFallbackTests` — encryption and transport are independent
  choices, so both transports share one negotiator and every combination of the two is covered,
  including the plaintext fallback that follows a refused MSE handshake.
- `TorrentTlsTests` checks SSL torrent transfers over TCP/uTP and certificate rejection using real
  TLS sockets. `I2pSamSessionTests` and `I2pPeerTransportTests` use a simulated SAM router for
  session lifecycle, discovery and peer transfers. These do not establish compatibility with
  external SSL peers or a live Java I2P/i2pd network.
- `KeepAliveTests` — pins the ordering between the protocol keepalive interval, the peer idle policy
  and the uTP inactivity timeout, rather than their literal values, so no layer can start giving up
  before the one above it.
- `SyntheticPeer*Tests` — a peer written for the tests that shares no code with the engine, including
  its own bencode. It exists because a conformant client cannot test the half of interop that matters
  most: libtorrent will never assign an extension an awkward id to see whether we route by it, send a
  bitfield of the wrong length, or hang up mid-handshake on request. Frames are kept as bytes, so the
  assertions run on what crossed the socket rather than on the engine's reading of it. Three interop
  defects found against a real libtorrent are pinned here, each confirmed to fail when the fix is
  removed.
- `LibtorrentOracleTests` — the same assertions, over the same synthetic peer, with libtorrent in
  PeerSharp's place. The synthetic peer is an independent opinion but not necessarily a correct one,
  and holding the engine to an invented standard is the original failure moved up one level. A
  reference implementation passing them is evidence they describe conformant behaviour; one failing
  would be a finding about these tests. Opt-in: `PEERSHARP_INTEROP=1` and a `client_test` built by
  the end-to-end harness.
- `LibtorrentCrashResumeTests` kills a PeerSharp downloader immediately before or after a resume
  snapshot becomes current. After restart without a forced recheck, it checks the initial BitTorrent
  bitfield and compares the bytes downloaded by libtorrent against the original payload. The newly
  saved piece crosses two files. PeerSharp then requests the missing pieces and a fresh libtorrent
  client downloads the complete torrent for byte-for-byte verification. Run both crash cases with
  `PEERSHARP_INTEROP=1`, the same `client_test` binary, and the following filter. On Linux,
  the Interop workflow builds a pinned libtorrent and requires both crash cases to pass.

```text
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Release -- --filter-method '*.ARestartServesExactlyThePublishedPieces'
```
