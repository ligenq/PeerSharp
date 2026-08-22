# Investigation Notes

Settled investigations, negative results and measurement baselines. These are retained because they
explain decisions and prevent old questions from being mistaken for new defects. They are not pending
work; genuinely open items live in [`FUTURE_IMPROVEMENTS.md`](FUTURE_IMPROVEMENTS.md).

---

## Property-based concurrency testing: what the parameters have to be to find anything

CsCheck's `SampleParallel` checks linearizability: it runs generated operations concurrently, then
asks whether the observed final state matches *any* sequential ordering of them. Whether it finds
anything depends entirely on how much interleaving the parameters buy.

Measured against `BandwidthChannel` with its atomicity deleted (`AddSaturating`'s compare-and-swap
loop replaced by a plain read-modify-write):

| `maxParallelOperations` | `iter` | `threads` | mutant | runtime |
| --- | --- | --- | --- | --- |
| 4 | 200 | 2 | **survived** | 0.7 s |
| 8 | 5000 | 4 | killed 5/5 | 0.9 s |

The first row is the Coyote failure repeating: a concurrency test that passes against an
implementation with its synchronisation removed. The cost of the second row is nil, so there is no
trade-off to weigh here — the weak settings simply bought nothing. `PieceState` needed the same
treatment: at `iter: 300` it killed its mutant 4 times in 5, at `iter: 2000` 8 times in 8.

Strengthening the bandwidth test then failed against unmodified code, roughly twice in thirteen runs,
which was a real defect rather than flakiness — see the CHANGELOG entry on the quota floor. CsCheck
shrank it to three operations and printed the valid orderings beside the observed state, which is
what made it diagnosable at all.

### The fix was also faster

Replacing the interlocked add-then-clamp with one short critical section, measured against the
previous implementation on the same machine, 80% spends / 18% refunds / 2% refills:

| threads | interlocked | `Lock` | |
| --- | --- | --- | --- |
| 1 | 43.8M ops/s | 57.4M ops/s | 1.31x |
| 2 | 14.0M ops/s | 27.6M ops/s | 1.97x |
| 4 | 11.3M ops/s | 25.7M ops/s | 2.26x |
| 8 | 7.8M ops/s | 23.3M ops/s | 2.98x |

Two nested compare-and-swap retry loops per call cost more under contention than taking a lock. The
lock-free version was neither correct nor fast; the assumption that it was cheaper is what kept it.

---

## What generative testing found that the examples did not

Two properties were added over pure, non-concurrent code, on the argument that the existing tests
were thin for the arithmetic involved - `FileMapper` had four tests for the offset arithmetic of
every read and write in the engine.

`FileMapper` failed immediately, and CsCheck shrank the counterexample to something a person can
check by hand: files of `[18, 0, 15]`, a 20-byte range from 0, of which only 18 bytes were covered.
An empty file shares its cumulative offset with the file after it, so the binary search resolving an
offset can return either; landing on the empty one gave a chunk size of zero, which the enumerator
read as the end of the range. The remaining bytes were dropped silently. That is a data-loss bug in
the write path - see the CHANGELOG entry - and it needs three files in a particular arrangement,
which is why no example test had it.

`PathValidator` did not fail, over 20,000 generated paths built from traversal sequences, UNC and
drive-rooted prefixes, reserved device names, unstorable characters and trailing dots. That is worth
recording as a negative result: the defences there are layered, and on Windows `..` is removed by the
trailing-dot rewrite before the explicit traversal check ever sees it. Removing any single guard
still yields a safe result, so the properties were confirmed to have teeth by removing all three at
once, which they caught.

A second round covered bencode's round trip, the DHT routing table and the block cache. The cache
failed the same way, and shrank just as usefully: read a 16 KiB block, write 16383 bytes over it,
read it again and get the bytes from before the write. Only whole aligned blocks are cached, so the
partial write was written through to storage and skipped here - without dropping the copy the cache
was still holding. Again a data bug, again in arithmetic around an edge case, again three operations
to reproduce and none of them exotic.

Bencode passed, which was worth confirming for one property in particular: keys must be written in
ascending *byte* order, because an info hash is the hash of the encoded dictionary and an encoder
that orders keys its own way computes a hash no other client agrees with. The current code sorts with
`StringComparer.Ordinal` over keys the parser decoded as Latin1, where one char is one byte, so
ordinal order is byte order. That is correct and it is fragile: changing the key encoding to UTF-8
would look like a tidy-up and would silently break every info hash the engine computes. The property
now pins it.

The routing table turned up only a contract wart - `GetAllNodes(0)` returned one node, because the
loop adds before testing its limit. Its one caller already refuses a limit of zero, so nothing was
reachable, but a limit a method does not keep is a trap for the next caller.

### A property test that exercised nothing

The file handle cache came through clean, and the check that this meant anything very nearly did not
happen. Deleting the reference-count guard from its eviction path - the mutation that should let it
close a handle somebody was still holding - failed no generated sequence at all, while an existing
example test caught it immediately.

Two reasons, both worth remembering. The cache floors its limit with `Math.Max(32, maxOpenFiles)`, so
the limit of 2 the test asked for was silently 32, and with five files eviction never ran once. And
even after raising the file count, the case where a careless eviction differs from a careful one only
arises when the *least recently used* handle is still leased, which needs more handles held at once
than the cache will keep - so the generator had to be weighted heavily towards acquiring before it
reached the state at all.

The lesson is not about this cache. A property test can pass for the same reason an empty test file
passes, and nothing in the output distinguishes the two. Only mutating the code says which one
happened, and it is worth doing before believing a clean run means anything.

The general lesson is the one the mutation work already suggested: the value is in the code where
arithmetic meets an awkward edge case, not in the code that looks most dangerous. Three of the five
subjects picked on that basis had a bug in them; the two chosen because they sounded dangerous, the
path validator and the DHT codecs, did not.

---

## Exceptions on the connect path, measured before changing anything

Exceptions being expensive in .NET is folklore worth checking rather than repeating. Measured on
.NET 10, no debugger attached:

| | cost |
| --- | --- |
| Return a failure result | 0.02 us |
| throw + catch, shallow | 0.81 us |
| throw + catch, 16 frames | 3.71 us |
| throw + catch `SocketException` | 1.47 us |
| exception across 8 awaits | 30.8 us (3.1 us without) |

So the folklore is stale for throughput, and the async case is the one that matters here.

What the engine actually threw, counted with the soak test's own first-chance counter:

| Scenario | notifications | distinct | amplification |
| --- | --- | --- | --- |
| Clean 8 MiB loopback transfer | 0 | 0 | - |
| 30s, 50 unreachable peers, TCP | 280 | 140 | 2.0x |
| 45s, 50 unreachable peers, uTP | 277 | 72 | 3.8x |

The happy path throws nothing; every one of these is a connection that did not happen. At those
rates the runtime cost is about a thousandth of a percent of a core - not worth changing on its own.
What made it worth changing is that each first-chance exception is a round trip to an attached
debugger, so a consumer stepping through their own application pays for how often this engine dials a
dead peer, and pays it as visible sluggishness. That was reported from Peerfluence.

The 2.0x on TCP is a floor, not a defect: one notification where the exception is raised and one as
it crosses the await. A standalone benchmark with the catch immediately around the await reproduced
exactly 2.0x, so no amount of restructuring gets below it while the API throws. Only not throwing
does.

libtorrent settles the design question. Its `peer_connection::on_connection_complete(error_code
const& e)` takes asio's error_code overload, so a refused connection is an ordinary branch costing no
exception at all. .NET has the same thing in `SocketAsyncEventArgs`, which reports through
`SocketError` where the task-based overloads throw.

After the change, with the same workloads: TCP 280 to 0, uTP 277 to 48. The 48 are
`TaskCanceledException` from the connect deadline firing before uTP exhausts its SYN retries, and
those were left alone - cancellation surfacing as an exception is the .NET contract, it is tested
here deliberately, and changing it would be fighting the language rather than the design.

The log counts confirm the drop is real rather than the engine having quietly stopped trying: over
the same 30 seconds it still logged 120 connect failures and 30 connect timeouts, and still completed
the transfer.

---

## Testing encryption against ourselves proved less than it looked

Every test of the MSE handshake ran our initiator against our own responder. That shows the two
halves agree with each other. It cannot show that either agrees with any other client, and the two
are not the same claim - a change that moves both halves the same way leaves every such test green.

Measured, by mutating the implementation and running both kinds of test against it:

| Mutation | Loopback tests (12) | Recorded qBittorrent handshake |
| --- | --- | --- |
| RC4 discard 1024 -> 1023 | 5 failed | failed |
| **keyA/keyB swapped in `InitRC4`** | **0 failed** | **failed** |

The second row is the whole argument. Swapping which derived key encrypts and which decrypts is
symmetric: run it against itself and everything still works, because both ends made the same
substitution. Run it against qBittorrent and nothing works at all. Twelve tests, none of which could
see it.

### Replaying a recording needs the key back

The handshake is randomised on both sides - private keys, padding lengths, padding contents - so a
captured exchange cannot simply be replayed: with a fresh private key the shared secret differs and
the recording decrypts to noise. `DiffieHellman` therefore gained an internal constructor taking a
private key, used only to replay, and the fixed key is written into the fixture. Our own outgoing
bytes are still not reproducible, and are not asserted; what the far side replied to was our public
key, which the fixed private key reproduces exactly.

Two things the capture got wrong first, both worth knowing before recording a protocol:

- RC4 decrypts **in place**. The first recording stored the same array it then decrypted, so the
  fixture held plaintext where it claimed ciphertext and replay decrypted it a second time. Record a
  copy.
- qBittorrent sends its BitTorrent handshake in a **separate segment** after the key exchange. A
  capture that stopped at `IsComplete` recorded the exchange and never the first thing the keys are
  actually used on, which is the part that proves the derivation.

The counterparty is a local qBittorrent rather than the public swarm: repeatable, version-stamped in
the fixture, and no stranger's bytes in the repository.

---

## The default listen port was unbindable, and nothing was listening on it

Two tests began failing with `SocketException: An attempt was made to access a socket in a way
forbidden by its access permissions` (`WSAEACCES`) on a machine where they had passed hours earlier,
with no relevant code change in between.

The cause was the host, not the engine. Windows reserves blocks of the dynamic port range for
Hyper-V, WSL and Docker, and `netsh int ipv4 show excludedportrange protocol=udp` showed
54981-55280 reserved — which contains the then-default `UdpPort` of 55125. Binding it directly:

| UDP port | result |
| --- | --- |
| 55125 (old default) | `AccessDenied` |
| 55080, 55181 | `AccessDenied` |
| 6881 (new default) | binds |

Nothing was listening on any of them. These reservations are assigned at boot and move, so a port
that works today can be unbindable tomorrow, which makes any fixed default in 49152-65535 a
liability rather than a choice. What other implementations do:

| Implementation | Default |
| --- | --- |
| libtorrent | 6881, then the next ports, then OS-assigned |
| qBittorrent | 6881 |
| Deluge | 6881-6891 |
| Transmission | 51413 (inside the dynamic range) |
| MonoTorrent | 0 — OS-assigned |
| PeerSharp, previously | 55125, no fallback, hard failure |

The number was changed to 6881, but the number is the smaller half of the fix: libtorrent's
behaviour of walking forward and then falling back to an OS-assigned port is what makes the choice
survive a host that has taken it. PeerSharp already writes the bound port back into settings and
announces from there, so the fallback needed no other plumbing.

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

## Making Stryker run at all: three constraints, each measured

**Settled.** Mutation testing is worth having here - applied by hand it twice overturned a conclusion,
once where 100% line coverage hid an untested branch and once where a concurrency test passed against
an implementation with its synchronisation deleted. Getting `dotnet-stryker` to run against this
repository took three specific accommodations, all of them recorded in configuration rather than
folklore.

**1. The VSTest runner hangs; MTP is required.** `xunit.v3` 4.0 runs on Microsoft.Testing Platform and
the .NET 10 SDK dropped the VSTest bridge. Stryker's default runner discovers all 2505 tests and then
sits there: measured at twenty minutes with every `testhost` and `vstest.console` process accumulating
under five seconds of CPU. `--test-runner mtp` works, and is marked preview by Stryker.

**2. The MTP runner cancels an initial test run that takes about three minutes.** The failure surfaces
as `Stryker.NET failed to mutate your project ... No test result reported`, which reads like a
discovery problem and is not one. The debug log names it exactly:

```
[DBG] "MtpRunner-0": Test run for "PeerSharp.Tests.dll" failed on attempt 1/2; discarding crashed server
System.Threading.Tasks.TaskCanceledException: A task was canceled.
   at Stryker.TestRunner.MicrosoftTestPlatform.AssemblyTestServer.RunTestsAsync(...)
```

Two attempts, roughly three minutes each. The whole suite takes 3m25s, so it never finishes. The
integration lane accounts for almost all of that, and it is compiled out of the assembly for mutation
runs only - `<Compile Remove="Integration/**">` under `StrykerRun`. What remains runs in 46 seconds.

**Excluding more than that is counterproductive, and this was measured too.** A first attempt also
removed the concurrency, robustness and interop lanes; every mutant in `LifetimeByteTotals` then came
back `NoCoverage`, because the tests that cover it live in the concurrency lane. Removing a lane
removes its coverage, and mutants report as untested rather than as findings.

**3. Half the mutants would not compile.** Nullable analysis is escalated to errors here, so Stryker's
rewrites trip CS8602 and friends and its "Safe Mode" discards the whole enclosing method: 11,992 of
25,169 mutants on the first run. `StrykerRun` reaches MSBuild as a global property and relaxes exactly
that list for the mutation job; ordinary builds stay strict, which
`dotnet msbuild -getProperty:WarningsAsErrors` confirms either way.

**4. Per-test coverage attribution is broken, and the default configuration reports a fictional
score.** A first full run over thirteen files returned 678 `NoCoverage`, 30 `Killed`, 0 `Survived` -
a 4.24% score. It is not a verdict on the tests: `PiecePicker` was reported as 215 uncovered mutants
while `PiecePickerTests` has 31 passing tests against it. The check that settles it:

| `coverage-analysis` | `LifetimeByteTotals` result |
| --- | --- |
| `perTest` (default) | 9 `NoCoverage` |
| `off` | 9 `Killed` |

Identical mutants, identical tests. So the tests do kill them and Stryker's MTP runner is failing to
attribute which test covers which mutant. `"coverage-analysis": "off"` is therefore mandatory here,
not a tuning choice, and a score produced without it should be discarded rather than investigated.

**Which is what bounds the scope.** Without per-test selection every mutant runs the whole 46-second
lane, so `stryker-config.json` names only small units where that arithmetic finishes: the rate
limiter, the lifetime counters, and the connection calculators. `PiecePicker`, `PathValidator`,
`DhtSecurity`, `DhtItemStore`, `FileMapper` and `PeerPriority` are all worth mutating and are left out
purely on runtime - roughly 700 mutants at 46 seconds each. Add them back the moment per-test coverage
works, not before.

Independently of the runtime limit, the transfer and networking paths are a poor fit regardless: a
mutant that merely makes a timing-dependent test flaky is reported as survived, which is noise wearing
the costume of a finding.

**The first real result: 147 mutants, 147 killed, 0 survived, in 56 minutes.** Every mutant across the
seven configured files was detected by an existing test - the rate limiter's budget arithmetic, the
lifetime counters, and all four connection calculators. Worth stating plainly because a perfect score
is usually a smell: it is not vacuous here, since each of the 147 ran the whole 46-second lane and
`Survived`, `NoCoverage` and `Timeout` were all zero. Eleven further mutants still fail to compile even
with the relaxed nullable settings, ten of them in `DhtQueryRateLimiter`; those are untested rather
than killed and are the honest asterisk on the number.

The result is a baseline, not a victory lap. These seven files were chosen partly *because* they are
well covered, so 100% says the configuration works and these units are genuinely pinned - not that the
engine as a whole would score anywhere near it. The files left out on runtime are where the interesting
answer is.

---

## Why Microsoft Coyote was removed

**Settled, by mutation testing. Historical: Coyote is no longer a dependency.** The decision was not
about its release cadence - it was that the suite explored far less than its presence implied, and the
cause was a version gap rather than anything about how the tests were written. The scenarios and
assertions survive unchanged behind `ConcurrencyStress`, so what the suite detects is exactly what it
detected before.

**Two things have to be true for a Coyote test to mean anything**, and neither holds by default here:

1. *The assembly must be rewritten.* Nothing runs `coyote rewrite` - not CI, not the build - so an
   ordinary `dotnet test` runs these as repeated stress runs on real threads. Rewritten, the engine
   reports controlling 4-5 operations; unrewritten, 1.
2. *The synchronisation under test must be a primitive Coyote recognises.* Coyote 1.7.11 targets
   .NET 8 and does not know `System.Threading.Lock` (.NET 9+), which is what this codebase uses almost
   everywhere. Without a scheduling point at the acquisition, a critical section is atomic as far as
   the explorer is concerned, and the interleaving that would break it is never generated.

**Measured, not inferred.** Against `LifetimeByteTotals`, whose whole purpose is that a removal and a
read cannot interleave:

| Implementation | Lock type | Systematic run |
| --- | --- | --- |
| correct | `Lock` | passes |
| removal and retirement as two steps | `Lock` | **passes** - the bug is invisible |
| no synchronisation at all | `Lock` | **passes** - still invisible |
| removal and retirement as two steps | `object` (Monitor) | fails, correctly |

A test that passes against an implementation with the locking deleted is not testing the locking.
`LifetimeByteTotals` was briefly switched to a `Monitor` lock to make that row reproducible; it went
back to `Lock` with the rest of the codebase once Coyote was removed, since nothing observes the
difference any more.

**Rewriting the library is not currently a way out.** Adding `PeerSharp.dll` to `rewrite.coyote.json`
does give Coyote scheduling points inside production code - it is what makes the `Monitor` row above
fail as it should - but classes that use `System.Threading.Lock` then block a controlled thread and
the deadlock monitor reports a hang. `DhtQueryRateLimiter` does exactly that. So the choice today is
between vacuous exploration and spurious hangs, and the config is left as it was.

**The two join patterns are mutually exclusive, which is worth knowing before anyone "fixes" one.**
Unrewritten, `Task.Run` is not controlled, so awaiting `Task.WhenAll` leaves the main operation
waiting on something Coyote cannot see and it reports a deadlock; every test fails. Rewritten, the
blocking `Task.WaitAll` that the whole suite uses is itself reported as a hang. So `WaitAll` is not a
mistake in the existing tests - it is the only pattern that works in the path CI runs. Converting to
`WhenAll` is a prerequisite for rewriting rather than an improvement on its own, and doing it without
also rewriting turns the suite red.

**What would change the picture:** a systematic explorer that models `System.Threading.Lock` - a
maintained Coyote, or a fork such as InterleaveX once it publishes packages. The scenarios are
unchanged, so adopting one means replacing `ConcurrencyStress.Run` and nothing else. Until then, read
the concurrency suite as stress rather than proof.

**Reproducing the table above** now requires restoring the dependency: add `Microsoft.Coyote` and
`Microsoft.Coyote.Test` to the test project and `microsoft.coyote.cli` to the tool manifest, write a
`rewrite.coyote.json` naming both `PeerSharp.Tests.dll` and `PeerSharp.dll`, then build, run
`dotnet coyote rewrite`, and run the `LifetimeTotals` tests. The measurements are recorded here
precisely so nobody has to.

---

## A per-IP connection cap cannot be defaulted on

**Settled, by measurement.** `ConnectionSettings.MaxConnectionsPerIp` was added to give
`AllowMultipleConnectionsPerIp` a middle ground - the flag can only permit one connection per address
or unlimited - and was initially defaulted to 8. That default rejects real connections.

`PexLiveExchangeTests` failed one run in six with it on, and passed 8 out of 8 with it off. The
mechanism was isolated rather than guessed at, by separating the two things the setting controls:

| Default | Address scan runs | Rejection possible | Result |
| --- | --- | --- | --- |
| 8 | yes | yes | 1 failure in 6 |
| 100000 | yes | no | 0 in 8 |
| 0 | no | no | 0 in 8 |
| (pristine, before the change) | n/a | n/a | 0 in 6 |

So it is the rejection firing, not the cost of the scan or a race it perturbs.

**Why 8 is reachable with three peers.** The cap counts live entries in `_connectedEndpoints`, and one
logical peer holds more than one for a while: a dial may try uTP and TCP, a handshake in progress is
already registered, and a reconnect overlaps the connection it replaces. Wherever peers genuinely
share an address the count runs well ahead of the peer count - and on loopback every engine is
`127.0.0.1`, so a local swarm is the worst case there is. `ManyPeerSoakTests` puts 24 leechers on one
address against a single seeder, which is the clearest statement of why no small default can be safe:
the engine's own test suite would be the first thing it broke.

**Left off by default.** The knob is worth having for a seedbox or an engine facing a swarm it does
not trust, where the operator knows what the address distribution looks like. A default cannot know
that, and the cost of guessing low is refusing real peers silently. Do not turn it on globally without
measuring against the deployment it is meant for.

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
