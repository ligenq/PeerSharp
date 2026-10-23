# Future Improvements

Deferred work and the evidence or consumer requirements needed before implementing it.
Completed changes are recorded in [the changelog](CHANGELOG.md).

These are ordered by likely impact. None should be implemented without the evidence or consumer
requirement named in its entry: each remaining change is architectural or has an ambiguous public API.

---

## 1. Observe cancellation in remaining Core tests

**Impact: low.** Some Core tests declare a timeout without observing
`TestContext.Current.CancellationToken`. xUnit1069 remains at `suggestion` in `.editorconfig`;
new tests should observe the token so timed-out work can stop. Prioritize tests that retain
sockets, files or background work after their deadline.

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

The current configuration disables per-test coverage attribution because it previously reported
covered mutants as `NoCoverage`. Every mutant therefore runs the mutation test lane. See the
[mutation testing instructions](tests/README.md#mutation-and-concurrency-testing) for the required
runner and build settings.

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

## 5. Conditional protocol extensions: BEP 38 and BEP 45

**Impact: low without a consumer.** These were identified during the BEP audit and remain intentionally
unimplemented:

- **BEP 38** (`similar` / `collections`) helps applications manage related torrents and reuse shared
  files.
- **BEP 45** (multiple-address DHT announce) matters for hosts intentionally announcing several public
  addresses.

**Resume when:** a consumer needs either feature. Its workflow should determine the public API rather
than exposing raw protocol fields speculatively.
