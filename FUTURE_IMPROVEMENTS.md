# Future Improvements

Known gaps that remain after the August 2026 cleanup. Completed work and measurement history live in
[`INVESTIGATION_NOTES.md`](INVESTIGATION_NOTES.md).

These are ordered by likely impact. None should be implemented without the evidence or consumer
requirement named in its entry: each remaining change is architectural or has an ambiguous public API.

---

## 1. xUnit1069: 398 Core tests with a timeout that never observe its token

**Impact: low, and deliberately deferred.** `xunit.v3` 4.0 added xUnit1069: a test with `Timeout`
should reference `TestContext.Current.CancellationToken`, so the timeout can stop the work rather than
only fail the verdict while it keeps running. 471 tests tripped it; the 73 in the lanes where that
costs something - integration, interop, concurrency and robustness - are fixed. Those bind sockets,
spawn client processes and repeat scenarios across threads, so a test that overruns its deadline holds
a port or a core while the next one starts.

The remaining 398 are in `Core`: in-memory, deterministic, and finishing in milliseconds. The timeout
rarely fires there, and when it does the abandoned work is a few allocations. The rule is held at
`suggestion` in `.editorconfig`; new tests should follow it.

**How the fixed ones were done**, for whoever picks up the rest: the token is threaded into whichever
awaited call accepts one, and where a test owns a `CancellationTokenSource` for its own deadline that
source is linked to the test's token rather than replaced - the deliberate deadline is usually the
point of the test. Shared polling helpers took a `CancellationToken` parameter and observe it in their
loop condition rather than in `Task.Delay`, so the descriptive `Assert.Fail` message survives.

**Resume when:** someone wants the Core lane tidy. There is no safe bulk edit - the token goes where a
signature accepts it, which needs the compiler's view of each call.

---

## 2. Widen the mutation-testing scope once per-test coverage works

**Impact: low today, and blocked on a tool rather than on us.** `stryker-config.json` mutates seven
small units. Six more are worth mutating and are left out purely on runtime: `PiecePicker`,
`PathValidator`, `DhtSecurity`, `DhtItemStore`, `FileMapper` and `PeerPriority` - roughly 700 mutants
between them.

The reason is that Stryker's per-test coverage attribution does not work against this suite, so
`"coverage-analysis": "off"` is mandatory and every mutant runs the whole 46-second lane. Measured
rather than assumed: identical mutants in `LifetimeByteTotals` report `NoCoverage` under the default
mode and `Killed` with attribution disabled. `INVESTIGATION_NOTES.md` has the table.

**Resume when:** a Stryker release attributes coverage correctly here - its Microsoft.Testing Platform
runner is still marked preview. Check by running the current config with the default coverage mode: if
the score stops being near-zero and `PiecePicker` stops reporting hundreds of uncovered mutants, add
the six files back and drop the `coverage-analysis` override.

---

## 3. Dependency-injection and hosting integration

**Impact: medium, and the shape is a decision rather than a detail.** The engine is constructed
through a static `ClientEngineFactory`. In a worker or ASP.NET host that means wiring lifetime,
configuration binding and logger plumbing by hand, every time. What is missing is an
`AddPeerSharp(IServiceCollection)`, `IOptions<Settings>` binding for the settings tree, and an
`IHostedService` that starts and stops the engine with the host.

Deliberately not done alongside the August 2026 hardening. It adds two package dependencies
(`Microsoft.Extensions.DependencyInjection.Abstractions`, and `Hosting.Abstractions` for the hosted
service) to a library that currently takes only `Logging.Abstractions`, and the useful questions are
ones a consumer's requirements answer rather than the engine's: whether an engine is a singleton or
can be keyed per configuration, whether `IHostedService` should block host startup on
`InitializeAsync`, and what a graceful stop should do about in-flight resume saves.

**Resume when:** a consumer is hosting the engine and can say what those three should do. The
dependencies are only worth taking on once the answers are known.

---

## 4. Per-piece and per-connection metrics

**Impact: low, and it is plumbing.** The `PeerSharp` meter covers engine aggregates: rates, session
totals, torrent and peer counts. The measurements a swarm problem is actually diagnosed from are not
there — pieces verified against pieces failing their hash, connection attempts by outcome, tracker
announces by result.

The reason is structural rather than an oversight. Aggregates are free: an observable instrument
reads the same `EngineStats` the public API already computes, so nothing is measured unless a
collector polls. Piece and connection outcomes live in per-torrent components (`FileTransfer`,
`PieceVerificationWriter`, `PeerManager`) that have no route to engine-level state, so recording them
means passing a metrics object down that graph and incrementing counters on paths that run per piece
and per connection.

**Resume when:** someone is operating the engine at a scale where a swarm problem needs diagnosing
from a dashboard rather than from logs. Thread one object through those three components; do not add
counters to per-block or per-message paths.

---

## 5. A domain error model

**Impact: low individually, cumulative for a consumer.** The library throws mostly framework
exceptions — `InvalidOperationException`, `ArgumentException`, `FormatException`,
`InvalidDataException`, `IOException` — against three domain types (`TorrentException` and its two
subclasses) plus `StorageException` and the tracker exceptions. A consumer cannot reliably tell
"tracker rejected the announce" from "disk full" from "malformed torrent file" without matching on
message strings.

Not done because it is a breaking change done properly and a cosmetic one done cheaply. Wrapping
everything in new types changes what existing `catch` blocks catch; adding types only at the public
entry points leaves the interior inconsistent. It wants doing once, with the entry points enumerated,
in a major version.

**Partly addressed from the other end.** The half of this that does not need a breaking change is
telling the library's own mistakes apart from the network's, and that is now done: see `Defect`.
Measured first — 183 of 358 catch sites catch `Exception` and 176 of those log and continue, so a
`NullReferenceException` thrown from the peer manager's maintenance loop let all 59 integration tests
pass while it fired on every tick. Only the unit test calling the broken method directly noticed.
Defects are now logged as errors with their stack and handed to an `IDefectObserver`, which the test
assembly registers so that any test provoking one fails.

**What remains here.** Five catch sites report; the other ~171 broad catches do not, so the sweep is
unfinished — and it is a judgement call per site rather than a mechanical edit, because catches
around *consumer callbacks* should keep swallowing everything. A consumer's bug must not take the
engine down. The typed-exception work above is untouched and still wants a major version.

**Resume when:** the next major version, or a consumer reports a case where the distinction actually
changed what their code should do.

---

## 6. Conditional protocol extensions: BEP 38 and BEP 45

**Impact: low without a consumer.** These were identified during the BEP audit and remain intentionally
unimplemented:

- **BEP 38** (`similar` / `collections`) helps applications manage related torrents and reuse shared
  files.
- **BEP 45** (multiple-address DHT announce) matters for hosts intentionally announcing several public
  addresses.

**Resume when:** a consumer needs either feature. Its workflow should determine the public API rather
than exposing raw protocol fields speculatively.
