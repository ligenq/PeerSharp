# Investigation Notes

Settled investigations, negative results and measurement baselines. These are retained because they
explain decisions and prevent old questions from being mistaken for new defects. They are not pending
work; genuinely open items live in [`FUTURE_IMPROVEMENTS.md`](FUTURE_IMPROVEMENTS.md).

---

## August 2026 improvement cleanup: implemented

The following former future-work entries were implemented together, starting with privacy and
observability risks:

- `ConnectionSettings.BindAddress` now covers inbound TCP/UDP, LSD, outbound TCP/uTP, SOCKS control
  and relay sockets, HTTP trackers, UDP trackers, web seeds, and exact-source magnet HTTP requests.
  A configured address never falls back to `Any`; port mapping is disabled because it cannot preserve
  a single-address guarantee. Hostname resolution still follows the operating system's DNS policy.
- `PeerDisconnectedAlert` exposes a departing peer's endpoint, client, final byte totals and reason.
- `MetadataDownloadStalledAlert` reports metadata-capable peers, request count and elapsed time when a
  swarm advertises metadata but sends no correctly sized response.
- End-game requests are capped at four copies per block, and adaptive connection timeout history is
  keyed by endpoint rather than IP address alone.
- Peer uTP and encryption preferences are persisted with session options, restored on startup, and
  carried through the magnet metadata rebuild. Only non-default preferences are written.
- Metadata request redundancy is configurable; transient metadata fetches have an operation-scoped
  progress overload; torrent-file adds can accept peer hints; and BEP 51 crawling can optionally emit
  duplicate sightings while retaining the unique-hash limit.
- Consumer API gaps were closed: nullable flow annotations on `TryParse`, readable torrent rates,
  safe torrent identity comparison, remote-byte guidance on `TorrentFile.LoadAsync`, and an explicit
  `IFiles.DownloadPath` contract.
- Bandwidth limits and reported rates now use signed 64-bit values consistently. Negative limits are
  rejected, and quota arithmetic saturates instead of overflowing.

The public API snapshot and deterministic tests were updated with these contracts.

---

## Test-suite residue

**One unidentified flake.** During the CI stabilisation work, one run in twenty-four failed and the
output captured only the summary line, not the test name. Eighteen consecutive runs passed afterwards
and the sweep that followed removed every fixed-duration wait in the CI-run subset, so it may already be
gone. If CI flakes again, `TestResults/*.trx` names the test even when console verbosity is minimal.

**Deliberate sleeps that remain.** Several tests still call `Task.Delay` before asserting — every one
asserts a *negative* (`Assert.False(task.IsCompleted)` and similar). Only elapsed time can establish
that something has not happened, and a slow machine makes those assertions stricter rather than
flakier. They are correct as written and should not be converted.

---

## Serving to Transmission: answered

**Settled.** PeerSharp serves a real Transmission correctly. `TransmissionInteropTests` drives
Transmission 4.1.3 as the only leecher for a locally generated torrent, and it takes the whole
payload - 64 MiB and 256 MiB runs, content verified by SHA-256 on the receiving side, over an
MSE-encrypted connection that Transmission chose. Transmission identifies us as `PeerSharp`,
becomes interested, unchokes and requests normally. The earlier live-session result stands
explained: the shortfall was swarm composition, not our serving.

**Time to first byte is Transmission's, not ours.** A peer connecting to a freshly started
Transmission torrent waits about ten seconds before Transmission expresses interest. That is
`RechokePeriod = 10s` in its peer manager: `rechokeSoon()` shortens the timer to 100 ms but is called
only from `tr_swarm::on_torrent_started`, so a peer arriving after that start waits out the full
period. Measured, not inferred: delaying our introduction by six seconds moved the connection but
left interest at the same absolute +10.2 s. Nothing to fix here, but it is worth knowing before
reading a slow first block as a fault.

---

## Transmission on Windows discards its send buffer on EWOULDBLOCK (upstream bug, fixed and verified)

**The defect.** `tr-buffer.h`'s `to_socket` compares a signed `send()` result against an unsigned
literal:

```cpp
if (auto const n_sent = send(sockfd, ..., n_bytes, 0); n_sent >= 0U)
{
    drain(n_sent);
    return n_sent;
}
```

On Windows `send()` returns `int`. `int` against `unsigned int` triggers the usual arithmetic
conversions, so a failed send of `-1` becomes `4294967295` and passes the test. Transmission then
treats the failure as a success, calls `drain(-1)` - which is `SIZE_MAX`, and `drain` does
`begin_pos_ += std::min(n_bytes, size())`, so it empties the *entire* output buffer - and reports
`SIZE_MAX` bytes written. Everything queued and unsent is silently thrown away, while its RC4 engine
has already advanced over all of it.

**It is Windows-only.** On POSIX `send()` returns `ssize_t`, which is wider than `unsigned int`, so the
literal promotes to signed and the comparison is correct. Only the LLP64 case, where `int` and
`unsigned int` are the same width, converts the -1. That is presumably why it has survived: it cannot
happen on Transmission's primary platforms.

**What it does to a peer.** The discarded bytes are a hole in the encrypted stream. Our RC4 keeps
counting, so from the hole onward every byte decrypts to nonsense, `TryDecodeMessage` reads a garbage
length and we drop the connection. Reconnecting costs most of Transmission's ten second rechoke period,
which is the 0.3 MiB/s staircase. `WSAEWOULDBLOCK` is what triggers it, so it needs a full socket
buffer - a peer that drains slower than Transmission fills.

**Verified by patching it.** Changing `n_sent >= 0U` to `n_sent >= 0` in a 4.1.3 build:

| Build | Payload | Time | Rate | Framing failures | Connections |
| --- | --- | --- | --- | --- | --- |
| stock 4.1.3 | 64 MiB | 140.6s | 0.5 MiB/s | 14 | many |
| patched | 64 MiB | 0.3s | 196.5 MiB/s | 0 | 1 |
| patched | 8 MiB | 0.5s | 15.6 MiB/s | 0 | 1 |

**Already fixed upstream, incidentally.** `main` carries the signed comparison in
`peer-socket-tcp.cc`, introduced by the socket refactor in commit `a919d47`, along with an explicit
`static_cast<int>` of the length for Win32. So the upstream action is a 4.1.x backport plus a
regression test, not a new fix.

**A correction to the earlier entry in this file.** This was first written up here as Transmission
serving its output buffer *out of order*, on the strength of finding that the wire carried ciphertext
belonging to a write queued 147 to 606 KB later. That observation was right and the mechanism was
wrong: a discard produces exactly the same measurement, because dropping queued bytes makes the wire
fall behind the queue, so a given wire offset then holds data queued later. The tell was in the data
and was missed - every one of the five deltas was positive, where genuine reordering would have
produced a mix of signs. Correct observation, wrong inference.

---

## Seeding to Transmission: settled

**It was our request queue depth against Transmission's refill timer.** Not a general ceiling on our
send path, and nothing to do with the Windows send-buffer bug above.

Transmission 4.1.3 only generates new block requests inside its bandwidth pulse, which runs every
500ms (`BandwidthTimerPeriod`, with `pulse()` calling `update_desired_request_count()` and
`maybe_send_block_requests()`). We answered an accepted batch in roughly 10-20ms and then had nothing
left to do, so the connection sat idle for the rest of the half second. The arithmetic matches what we
measured: 250 requests times 16 KiB over 500ms is about 8 MiB/s, and we measured 7.4.

Two things compounded it. Our queue held 250, and we were not advertising `reqq`, so Transmission
applied its default assumption of 500 (`peer_reqq_.value_or(PeerReqQDefault)`) and everything above
250 came back rejected - a traced 16 MiB seed sent 1,525 requests for 1,024 blocks with 501 refused.
MonoTorrent advertises 256 and hits the same ~7.5 MiB/s ceiling for the same reason, which is the
cleanest confirmation available that this was never specific to us.

**Fixed on both counts.** We advertise `reqq` now, and the depth is 2000 - matching what libtorrent
advertises, and enough that one pulse's worth of requests takes longer to drain than the pulse itself:

| Depth | Seeding to Transmission, 64 MiB | Requests refused |
| --- | --- | --- |
| 250, not advertising | 7.4 MiB/s | 501 per 16 MiB |
| 2000, advertising | 31.6 MiB/s | 0 |

**The depth is cheaper than it looks, which is why 2000 is defensible.** The obvious worry is that
2000 outstanding requests means 2000 blocks buffered per peer, or about 31 MiB, multiplied across a
swarm. It does not: the queue is a bounded channel of 12-byte descriptors and `ExecuteUploadItemAsync`
reads each block lazily as it is served, so the depth costs about 23 KiB per peer, or 4 MiB across two
hundred. In-flight block data is bounded by the send queue instead, which is unchanged. `ManyPeerSoak`
measures this: at 24 peers, 250 and 2000 are indistinguishable in both aggregate throughput and heap
high-water mark.

**Transmission fixes its side on main too.** `peer-msgs.cc` there refills the request window as soon as
piece data arrives rather than waiting for the pulse, so this ceiling disappears against any client
built from main regardless of what we do. Both halves are worth having: theirs removes the timer, ours
removes the guessing.

---

## Stopping a torrent does release memory, but not to the operating system

**Reported as a leak, and it is not one.** Stopping a torrent looks in Task Manager as though nothing
is given back. Measured with the sample - download 512 MiB at 6 MiB/s, stop at 40 seconds, then force
a collection either side:

| | heap | GC committed | working set |
| --- | --- | --- | --- |
| running | 13.2 MiB | 36.3 MiB | 80.5 MiB |
| stopped, ordinary collection | 3.4 MiB | 16.8 MiB | 62.1 MiB |
| stopped, compacting collection | 3.5 MiB | 4.0 MiB | 49.2 MiB |
| baseline before the torrent | 1.2 MiB | - | 39.3 MiB |

The managed heap returns to about 3.5 MiB either way, so nothing is retained that should not be: there
is no leak in the library. What stays behind is *committed* memory. An ordinary collection frees the
objects and leaves the segments committed, because the runtime expects to need them again - and
committed segments are what a process monitor reports.

**A compacting collection gives most of it back.** Setting
`GCSettings.LargeObjectHeapCompactionMode = CompactOnce` and collecting gen2 with `compacting: true`
takes committed memory from 16.8 MiB to 4.0 MiB and the working set from 62 to 49. The ten MiB still
above the idle baseline is JIT-compiled code, loaded assemblies and thread stacks, which do not come
back regardless.

**Where that belongs.** Not in the library. A library that forces a blocking compacting collection is
making a decision about the whole process on behalf of an application that may be doing something else
at the time. The host knows when it is idle; PeerSharp does not. An application that wants the memory
back after stopping should do it itself, and it is cheap here because the heap is small by then - a
compacting gen2 over about 12 MiB.

**Reproducing it.** `--stop-after <seconds>` in the sample stops the torrent, keeps reporting, and
prints heap, committed and working set either side of a forced collection.

---

## Allocation churn during transfers, measured and not yet attributed

**The numbers, reproducible with the sample.** One instance seeds, another leeches by address with
the DHT and local discovery off, rate limited so the transfer lasts long enough to sample:

| Path | Allocated per byte moved | Per 16 KiB block |
| --- | --- | --- |
| Leeching | 0.30 | about 4.9 KiB |
| Seeding | 0.18 | about 2.9 KiB |

```
peersharp-cli torrent --out seed --seed --recheck --no-dht --diagnostics
peersharp-cli torrent --out leech --no-dht --peer 127.0.0.1:55125 --down 6144 --diagnostics
```

**There is no memory problem.** The heap is flat across a 512 MiB transfer - 24.6 to 27.2 MiB, back
to 18.5 on completion - working set holds near 79 MiB, and nothing leaks. A heap snapshot mid transfer
is 12.3 MiB, three quarters of it the 562 pooled 16 KiB block buffers doing their job. Reducing the
churn would buy less GC pressure, not a smaller footprint, which is a much weaker reason to touch a
working transfer path.

**Four hypotheses tested and eliminated**, each by changing one thing and re-measuring:

- The reader's buffer size. At 16 KiB rather than four blocks the ratio goes to 0.32, marginally worse
  and nowhere near explaining it.
- The piece buffer pool's depth. `maxArraysPerBucket: 4` looked like a strong candidate, since a fifth
  concurrent piece allocates a fresh 256 KiB buffer and 0.3 x 2048 pieces x 256 KiB lands almost
  exactly on the measured total. Raising it to 32 changed nothing: 0.318.
- The DHT, which an earlier entry wrongly blamed for a startup spike and which was retracted.
- A single hotspot at all. Both directions allocate in proportion to blocks moved, which is what rules
  this out: a hotspot in the receive path could not also account for the seeder's 0.18.

**What it would take to go further.** Allocation-tick attribution. `dotnet-trace --profile gc-verbose`
collects the events but its speedscope export is a time-based call tree and drops the byte weights, so
the analysis needs PerfView or something built on TraceEvent. Guessing at candidates and re-measuring
has now cost four rounds for four negatives, and the shape of the result - a few KiB per block on both
paths, heap flat - looks like ordinary per-message overhead spread across async state machines,
message objects and pooled-buffer bookkeeping rather than one thing worth cutting out.

**Recommendation: leave it.** Revisit only with real attribution data, and only if GC pause time shows
up as a problem on a machine that matters.

---

## Interop baseline

Re-measured after the session that found the Transmission fault. Every earlier figure in this file was
taken either with that fault active - so it timed a broken connection rather than our throughput - or
before the `RateLimitedStream` short-write fix, and none of them should be quoted.

All arms: 64 MiB of random data, MSE encryption, loopback, one machine, Mullvad up throughout.

| Seeder to leecher | Rate | Notes |
| --- | --- | --- |
| Transmission to MonoTorrent | 252.6 MiB/s | control arm, one connection, zero drops |
| PeerSharp to qBittorrent | 108.2 MiB/s | |
| qBittorrent to PeerSharp | 64.0 MiB/s | |
| PeerSharp to PeerSharp | 58.0 MiB/s | both engines in one process |
| PeerSharp to Transmission | 31.6 MiB/s | was 7.4 before reqq and the deeper queue |
| Transmission to PeerSharp, patched | 196.5 MiB/s | one connection, zero framing failures |
| Transmission to PeerSharp, stock 4.1.3 | 0.5 MiB/s | measures the Windows send-buffer bug, not us |

**Many peers.** `ManyPeerSoakTests` covers what these single-connection numbers cannot: 24 leechers against one seeder complete in 12.2s at 31.4 MiB/s aggregate. It reports heap against peer count rather than asserting a threshold, because the right ceiling depends on the deployment.

**Two things worth not misreading.** The PeerSharp-to-PeerSharp control arm is *slower* than talking to
qBittorrent because both engines share one process and compete for the same cores; it is a harness
artefact, not a finding about the code. And these are single-peer loopback figures - far above any real
link, and silent about the cost that actually scales, which is many peers at once. Nothing here
measures that, and a swarm-sized harness is what a real performance effort would need to start from.

---

## August 2026 remaining-work implementation

The three previously conditional architecture items were implemented after their policies were chosen:

- Direct, unbound HTTP and UDP trackers announce independently over IPv4 and IPv6. An explicit
  `BindAddress` remains strict and single-family, while tracker proxies retain proxy-selected address
  behavior. Failure of one direct family does not discard a successful response from the other.
- A running magnet now detaches established peers during metadata initialization, retains raw
  bitfield/HAVE/HAVE_ALL/HAVE_NONE state while the piece count is unknown, resizes and replays that
  state, and adopts safe live sockets into the replacement `PeerManager`. Missing, malformed, or
  inconsistent availability uses the safe close-and-rediscover fallback; preview mode preserves no
  live sockets because it intentionally stops after metadata.
- Metadata requests are peer-owned. Eligible peers independently select least-requested missing
  pieces; timeouts, rejects, disconnects, and attempt limits are tracked per peer/piece. The pipeline
  still caps distinct pieces and `MetadataRequestRedundancy` caps simultaneous owners of one piece.

The final test-hardening pass added ten direct regression tests: three HTTP tracker family-policy
tests, two UDP tracker family-policy tests, two live-peer handoff/fallback tests, and three metadata
scheduling ownership tests. A combined focused run of the affected tracker, peer, metadata, and
architecture groups passed all 333 tests.

The final regression run passed 2,300 core tests with 24 environment-dependent skips (2,324 total),
plus all 78 WebTorrent tests. The full solution build completed with zero warnings and zero errors.

**Caveat on all of these.** One machine, one version of each client, loopback, and a Mullvad tunnel up
throughout - the interop tests record the machine's interfaces at the start of each run for exactly
that reason. Loopback RTT is far below what any network-tuned heuristic expects, so the absolute
figures are not a benchmark. The comparison across counterparties is the result.
