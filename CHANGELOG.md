# Changelog

Notable changes per release. Entries describe what a consumer of the library would notice; the commit
history has the reasoning and the measurements behind each one.

## Unreleased

A review of the engine outside its protocol code: what happens when the host dies mid-write, what a
stranger can make the DHT server spend, and what a consumer can see from outside the process.

### Upgrade notes

- **Resume data is now validated before it is adopted.** A resume file whose recorded info hash names
  a different torrent, or whose recorded piece size, content length or bitfield length disagrees with
  the torrent, is discarded, and the torrent starts from an empty bitfield rather than claiming pieces
  it never verified. Resume data written by a newer format version is discarded for the same reason.
  A magnet cannot be checked when it is added, so the same validation runs again once its metadata
  arrives. Files written by 3.2.0 and earlier are accepted unchanged — they already carried the fields
  this checks, nothing read them. A missing hash or geometry field means "not recorded" and is not
  grounds for rejection.
- **Saving resume data now flushes piece data to the disk first.** Saves cost an fsync of the files
  written since the last save. If the flush fails, that torrent's resume data is skipped for that
  round and the previous copy is left in place; the save is retried on the next interval.
- **Partial pieces in resume data are capped at 16 MiB in total**, not just at 32 pieces. Torrents
  with piece sizes above 512 KiB carry fewer partial pieces across a restart than before and
  re-request those blocks. Torrents at ordinary piece sizes are unaffected.
- **A DHT node stores peers for at most 2000 info-hashes.** Beyond that, announces for hashes not
  already held are answered but not recorded. This bounds the store; it does not change what the
  engine's own torrents can find.
- **Inbound DHT queries are budgeted at 60 per source address per minute.** Over-budget queries are
  dropped without a reply, and the sender is not added to the routing table. Well above what an
  iterative lookup from one node costs. Once more than 20,000 addresses are being tracked at once,
  sources that cannot be tracked individually draw on a shared allowance of 600 queries per minute
  rather than being waved through.
- **The default listen port is now 6881, was 55125**, for both TCP and UDP. 55125 sat inside the
  dynamic range (49152-65535), which the OS allocates outbound connections from and which Windows
  reserves blocks of for Hyper-V, WSL and Docker; a bind inside a reserved block fails with a
  permission error although nothing is listening, and the blocks move between reboots. 6881 is the
  first of the range BitTorrent has used since the original client and the default in libtorrent,
  qBittorrent and Deluge. Anyone forwarding a port through a router, or relying on the old default
  from outside the process, should set `ConnectionSettings.TcpPort` and `UdpPort` explicitly.
- **A listen port that cannot be bound no longer stops the engine starting.** The configured port is
  tried, then the next ten, then an OS-assigned one, with a warning naming the port actually bound.
  The bound port is written back to the settings and announced from there, so trackers and the DHT
  stay consistent. Previously a busy or reserved UDP port failed startup outright.

### Added

- `ConnectionSettings.MaxConnectionsPerIp` — how many connections one address may hold on a torrent.
  The middle ground `AllowMultipleConnectionsPerIp` lacked, which could only allow one connection per
  address or unlimited. **Off by default**: it counts live registrations, and a single logical peer
  briefly holds more than one while a dial tries both transports or a reconnect overlaps the
  connection it replaces, so any non-zero value has to be set from what the deployment looks like.
- Metrics, on a `Meter` named `PeerSharp` — aggregate rates, lifetime byte totals, torrent counts and
  connected peers. Subscribe with `builder.AddMeter(PeerSharpMetrics.MeterName)`. Every instrument is
  observable, so nothing is measured unless a collector polls and a process that never subscribes
  pays nothing. Each engine's meter carries itself as `Meter.Scope`, so several engines in one
  process stay distinguishable. The byte counters cover the engine's whole life, including torrents
  since removed, so they only ever increase — `EngineStats` still reports the torrents present now.

### Fixed

- **An unclean shutdown could leave resume data claiming pieces whose bytes were never written.**
  Resume data was written durably — temp file, flush to device, atomic rename — while piece data was
  handed to the operating system and never flushed, so the durable half was the claim and the
  volatile half was the data it claimed. Piece verification runs when a piece arrives and never
  again, so nothing downstream would have caught it: the engine would restart, trust the bitfield and
  serve whatever the disk held.
- **A malformed or stale resume file could throw while loading partial pieces.** Piece indices and
  block counts read out of the file were used to index arrays without being checked against the
  torrent, so a truncated write produced a negative copy length rather than a rejected piece.
- **The number of info-hashes a DHT node stored peers for was unbounded.** Peers per hash were capped
  at 200 and the number of hashes was not, so the size of the table was decided by whoever was
  announcing.
- **Inbound DHT queries had no per-source budget.** Answering costs a parse, a routing-table walk and
  a reply larger than the query, and the source address of a UDP datagram is unverified.
- **Sequential downloads rescanned the completed prefix on every pick**, so the scan grew with
  progress and was longest when the torrent was nearly done.
- **Resume data could claim a piece that finished during the save.** The bitfield was captured after
  the flush rather than before it, leaving a window in which a piece completing in between was
  recorded as durable without having been flushed. Saves for one torrent are also serialised now, so
  two overlapping saves cannot write their snapshots in the opposite order to the one they were taken.
- **Deselecting a file dropped its pending flush.** A file written and then deselected before the next
  save had its dirty flag cleared without a durability barrier, while the completed pieces covering it
  stayed in the bitfield and were persisted as present.
- **The block cache could serve bytes that had since been overwritten.** Only whole aligned 16 KiB
  blocks are cached, so a write that covered part of a block was written through to storage and then
  skipped - leaving any cached copy of that block holding pre-write bytes, which the next aligned read
  returned in preference to storage. Reachable whenever a partly-covered block had been read before:
  the last block of a torrent is partial, and repair and end-game rewrite blocks that have already
  been served. The stale bytes went to whichever peer asked for that block. Every block a write
  touches is now either refreshed in full or dropped from the cache. Found by the new property tests
  over `BlockCache`.
- **A torrent containing an empty file could silently lose data.** Empty files are legal and nothing
  filtered them out, but one sitting between two other files shares a cumulative offset with its
  neighbour, and the binary search resolving an offset to a file could settle on the empty one. It
  has no room, which the range walk read as the end of the range: every byte after the empty file was
  dropped from the operation list without an error. Storage writes exactly that list, so the tail of
  each affected block was never written, while the piece it belonged to was hashed in memory,
  verified and recorded as present. Found by the new property tests over `FileMapper`.
- **Bandwidth quota could be spent past its floor.** Spending added and then clamped as two separate
  interlocked steps, so a spend large enough to break the debt floor could be lifted back above it by
  a refund arriving in between; the clamp re-read a value that no longer needed clamping and the floor
  went unenforced. Quota is spent from a deliberately lock-free path while a tick loop refills it, so
  the interleaving is reachable. Each operation now commits in one step, which also made the channel
  faster under contention — the previous version retried two nested compare-and-swap loops per call.

### Changed

- **Dependencies updated across the solution**, which needed three follow-ups. `xunit.v3` 4.0 moved to
  Microsoft.Testing.Platform and dropped VSTest support on the .NET 10 SDK, so `dotnet test` is opted
  into the platform's own mode via `global.json` and the CI lanes use its filter and TRX options.
  SonarAnalyzer 10.32 added two rules that a `-warnaserror` build treats as errors; all 37 sites are
  fixed, one of which was a false positive and is suppressed with the reason recorded. xunit's new
  xUnit1069 fires on 471 existing tests; the 73 in the lanes that hold real resources - integration,
  interop, concurrency and robustness - now observe the token, so a test that overruns its deadline
  stops instead of holding a port while the next one starts. The 398 in-memory Core tests are held at
  `suggestion`, where an overrun costs a few allocations.
- **Microsoft Coyote removed.** Its last release was March 2024, but the reason for dropping it is
  what measurement showed rather than its release cadence: nothing ran `coyote rewrite`, so the engine
  was executing each scenario repeatedly rather than exploring interleavings, and Coyote 1.7.11 does
  not model `System.Threading.Lock`, which this engine uses almost everywhere. A test passed against an
  implementation with its synchronisation deleted outright. The scenarios and their assertions are
  unchanged and now run through a plain repetition harness, so what the suite actually detected is
  exactly what it detected before - it is just no longer described as something it was not.
- **Mutation testing added**, weekly and non-blocking, via `stryker-config.json` and a `Mutation`
  workflow. Scoped to seven small pure-logic units - the DHT query rate limiter, the lifetime byte
  counters and the connection calculators - where a surviving mutant is unambiguously a test gap. The
  first run killed all 147 mutants. Three things make it work and are documented where they are set:
  `--test-runner mtp` (xunit.v3 4.0 is MTP-only, and Stryker's VSTest runner hangs), the integration
  lane compiled out of the assembly for mutation runs (Stryker's runner cancels an initial run of about
  three minutes), and `"coverage-analysis": "off"` (its per-test attribution reports covered code as
  uncovered here, producing a fictional score).
- The interop suite runs nightly in CI. It is the lane that found the late bitfield, the dead-socket
  plaintext fallback and the inbound uTP rejection, and no CI job ran it. Tests whose counterpart
  client is not installed skip rather than fail.

## 3.2.0 — 2026-08-16

Two things drove this release: a host routing through a VPN had no way to make the engine stay on
that interface, and a magnet's metadata fetch was scheduled per piece, so one silent peer could hold
a piece's only timer while capable peers sat idle.

### Upgrade notes

**This release changes signatures. Source changes are required.** Bandwidth limits and reported
transfer rates are signed 64-bit throughout, where they were a mix of `int` and `uint`. Recompiling
is enough for most callers; anything that stores these values in an `int`, or implements `ITorrent`
or `IBandwidth`, needs editing. This is a minor version rather than a major one by choice, so a
binary-only upgrade of a pre-compiled consumer can fail at runtime — rebuild against the new
package rather than swapping the assembly.

The members that changed:

- `TransferSettings.MaxDownloadSpeed` / `MaxUploadSpeed`: `uint` → `long`.
- `FilesSettings.MaxDiskReadSpeed` / `MaxDiskWriteSpeed`: `uint` → `long`.
- `ITorrent.DownloadLimitBytesPerSecond` / `UploadLimitBytesPerSecond` and the two disk equivalents:
  `int` → `long`.
- `AddTorrentOptions.DownloadLimitBytesPerSecond` / `UploadLimitBytesPerSecond`: `int?` → `long?`.
- `SavedTorrentOptions` limit fields, `PeerInfo` and `EngineStats` speeds, `TransferStats.DownloadSpeed`
  / `UploadSpeed`, and `TransferStatsAlert` speeds: `int` → `long`.
- `IBandwidth`: every limit parameter and the tuples returned by `GetTorrentLimits` and
  `GetTorrentDiskLimits`.

Negative limits are now rejected with `ArgumentOutOfRangeException` where they were previously
clamped to zero or accepted and mishandled. A caller passing a computed limit that can go negative
needs to clamp it before the call.

Also worth reading before upgrading:

- **`ConnectionSettings.BindAddress` disables port mapping while it is set.** UPnP and NAT-PMP open
  their own interface-selected sockets and cannot honour a single-address guarantee, so they are
  turned off rather than allowed to leak. Setting `BindAddress` to `IPAddress.Any` or `IPv6Any` now
  throws; use `null` for the previous listen-on-everything behaviour.
- **Direct trackers announce over IPv4 and IPv6 independently.** A tracker reachable both ways now
  sees two announces per interval instead of one, and one family failing no longer discards the
  other's peers. A configured `BindAddress` stays strict and single-family; proxied trackers keep
  proxy-selected addressing.
- **`MetadataMaxRequestAttempts` orders who gets asked rather than capping the total.** When every
  peer has been set aside for every missing piece the budgets are restored, so a magnet is never left
  with willing peers connected and nothing scheduled. An explicit reject survives restoration; a
  timeout does not, since a slow link produces one just as readily as an unwilling peer.

### Added

- `ConnectionSettings.BindAddress` — binds inbound TCP/UDP, LSD, outbound TCP/uTP, SOCKS control and
  relay sockets, HTTP and UDP trackers, web seeds, and exact-source magnet fetches to one local
  address. Socket creation fails rather than falling back to an unbound socket, which is what makes
  it usable as a kill switch. OS name resolution is outside the binding and follows the host resolver.
- `PeerDisconnectedAlert` — a departing peer's endpoint, client name, final byte totals and reason
  code, which the current-peer snapshot cannot report once the peer is gone.
- `MetadataDownloadStalledAlert` — fired once when metadata-capable peers have been asked repeatedly
  over an extended period without a single piece arriving.
- `IClientEngine.GetMagnetMetadataWithProgressAsync` — the transient metadata fetch with
  operation-scoped progress, without adding the torrent to the engine or its alert stream.
- `ITorrent.DownloadSpeed` / `UploadSpeed` and `ITorrent.HasSameIdentity`, which compares V1 and V2
  info hashes while treating an absent hash version as no evidence.
- `AddTorrentOptions.AdditionalPeers`, for carrying BEP 9 `x.pe` hints from a previewed magnet into
  the subsequent torrent-file add.
- `TransferSettings.MetadataRequestRedundancy` — how many peers may hold a request for the same
  metadata piece at once. Default 3.
- `DhtIndexerOptions.ReturnDuplicateSightings`, for BEP 51 crawls that want repeat sightings as a
  ranking hint. `MaxInfoHashes` still counts unique hashes.
- Peer uTP and encryption preferences are persisted with session options and restored on startup.
  Only non-default preferences are written.
- Nullable flow annotations on `MagnetLink.TryParse` and `TorrentFile.TryParse`.

### Changed

- Metadata requests are peer-owned. Each eligible peer independently picks a least-requested missing
  piece, and timeouts, rejects, disconnects and attempt limits are tracked per peer and piece. The
  pipeline still caps distinct pieces in flight and `MetadataRequestRedundancy` caps simultaneous
  owners of one piece.
- A running magnet keeps its established peer connections across metadata initialization. Bitfield
  and HAVE state that arrived while the piece count was unknown is retained, resized and replayed;
  anything missing or inconsistent falls back to close-and-rediscover. Preview mode keeps no live
  sockets, since it stops after metadata by design.
- End-game requests are capped at four copies per block.
- Adaptive connection-timeout history is keyed by endpoint rather than by address alone, so peers
  behind one NAT or seedbox no longer share a latency and success record.
- Quota arithmetic saturates instead of overflowing.

### Fixed

- A magnet could stall permanently with capable peers connected. Metadata attempt budgets never
  expired, so enough timeouts spent every peer/piece pair and left nothing schedulable — reached by
  ordinary latency against the one-second default timeout, not by malice.
- Corrupt metadata could blacklist a whole swarm. Every peer credited with an answer was refused on
  hash failure, including the peers whose copies were harmless duplicates. Only the peers whose bytes
  were actually stored are refused now.
- A peer could grow unbounded memory during a magnet's pre-metadata window by sending HAVE messages,
  whose indices cannot be range-checked before the piece count is known. Deferred availability is now
  bounded, and a peer that exceeds it is rediscovered rather than adopted with a partial record.
- HTTP tracker and web-seed connections try every resolved address of the family instead of stopping
  at the first, so a tracker behind DNS round-robin survives one host being down.
- LSD joins its multicast group on the configured bind interface. Binding the socket alone left group
  membership to the routing table, so announcements on the bound network were never received.
- `EngineStats` and per-torrent rates no longer truncate above 2 GB/s.

## 3.1.0 — 2026-08-09

65 commits since 3.0.0. Most of this release came out of running the library against real swarms and
real third-party clients rather than from feature work, so the bulk of it is under Fixed.

### Upgrade notes

Read these three before upgrading — each changes behaviour without changing a signature.

- **`ITorrent.Finished` now reports `false` until metadata arrives.** It was "every piece received",
  which an empty piece collection satisfies, so a magnet reported itself finished before it knew its
  own piece count. Anything branching on `Finished` for magnets will behave differently, and the
  library's own connection, choking and queueing decisions were among the things getting it wrong.
- **`MetadataRequestPipeline` default 8 → 32** and **`MetadataRequestTimeoutSeconds` default 3 → 1.**
  A consumer that never set these gets noticeably more aggressive magnet metadata fetching on
  upgrade. Both are still settings; the previous values can be restored.
- **Filenames the platform cannot store are now rewritten instead of dropped.** A path containing
  `|`, `:`, `*`, `?`, `"`, `<` or `>` — legal on Linux, not on Windows — previously caused that file
  to be skipped while the torrent still reported 100% complete. Such names now have the offending
  characters replaced with `_`, and a reserved name like `CON.txt` becomes `CON_.txt`. Path traversal
  is unaffected and still refused outright.

`IPeers` and `IPiecePickerContext` each gained a member. Both exist to be consumed rather than
implemented, so this is a minor release; code that implements or mocks either interface will need the
new member.

### Added

- `IPeers.Add(IEnumerable<IPEndPoint>)` — offer peer addresses alongside whatever discovery finds.
  Offered, not forced: they pass the same blocklist, limits and duplicate checks as any candidate.
- `IPiecePickerContext.IsSnubbed`, so a custom picker can steer snubbed peers as the built-in does.
- `ConnectionSettings.AllowMultipleConnectionsPerIp` and `ConnectionSettings.PexInterval`.
- A sample CLI (`samples/PeerSharp.Cli`) used as the diagnostic harness for most of this release.
  Not part of the NuGet package.

### Changed

- Advertises BEP 10 `reqq` and accepts a much deeper request queue. Peers previously had to guess,
  and Transmission's guess of 500 against our actual 250 meant every request above the limit came
  back rejected. Seeding to Transmission went from 7.4 MiB/s to 31.6 MiB/s.
- Reads and honours the BEP 10 fields that were being parsed and discarded.
- Sends `ut_pex` rather than only consuming it.
- Randomises the peer id per outgoing connection.
- Snubbed peers are steered towards common pieces rather than rare ones, as libtorrent does.

### Fixed

**Peer connections**

- A completed torrent no longer redials peers it cannot trade with. Confirmed seeds are excluded
  while complete, addresses with port 0 are refused, and half-open depth is bounded by the peer
  deficit once downloading is done. A 33-minute run had made 28,621 outgoing connections, 54% of
  which moved no bytes in either direction.
- A connection is judged by what it moved, not by whether it connected, so a peer that handshakes
  cleanly and does nothing now earns the backoff it should always have earned.
- Addresses that served data failing a hash check are remembered, instead of the count dying with
  the connection and the peer returning to the front of the queue.
- An incoming connection's ephemeral source port is no longer treated as a dialable peer address or
  gossiped onward through PEX.
- A rate-limited write can no longer stop short silently.

**DHT**

- IPv4 and IPv6 are kept in separate routing tables, per BEP 32. A dual-stack node uses one id in
  both, so a single table let whichever family answered last erase the other family's address.
- Bootstraps over IPv6 as well as IPv4, and asks for the address families it can actually reach.
- Announces at all — a key collision meant the announce was never sent — and then to a bounded
  number of nodes rather than to every node a walk reaches.
- Keeps asking for peers while a torrent still needs them, and asks again promptly when it has none,
  rather than once against an empty table.
- One unreachable node no longer aborts an unrelated UDP receive on Windows (SIO_UDP_CONNRESET).

**Metadata and magnets**

- Metadata requests prefer peers that have actually answered before.
- A magnet is no longer treated as complete before it has metadata, which had stopped it dialling
  the very seeds holding what it needed.
- `ClientEngine.GetMagnetMetadataAsync` runs its torrent invisibly: no alerts, no session entry, no
  queue participation, no claim on the info hash.

**Trackers and web seeds**

- A private torrent refuses trackers from outside its own metadata (BEP 27).
- Echoes the tracker's session token.
- Stopped sending BEP 7's `ipv6` parameter, which is discouraged, spoofable, and defeats a tracker
  proxy. Trackers infer the family from the connection.
- A failed web seed is retried in fifteen seconds rather than four hours — a unit mix-up.

**Transfer**

- A piece that fails its hash check is retried from one peer at a time, and the peers that supplied
  it are read before the piece is reset. The strike loop that credited them had never run.
- A piece reservation is released when its peer disconnects.
- Fixed three defects in the uTP delay estimate.

**Logging**

- Two ordinary events had been reported as faults; a gateway ignoring NAT-PMP no longer prints a
  stack trace; connection closes record which method caused them.

### Interop

Verified against Transmission 4.1.3 and qBittorrent, 64 MiB over MSE on loopback. PeerSharp to
qBittorrent 108.2 MiB/s, qBittorrent to PeerSharp 64.0 MiB/s, PeerSharp to Transmission 31.6 MiB/s.
Leeching from a stock Transmission 4.1.3 on Windows measures 0.5 MiB/s because of an upstream
send-buffer bug, fixed upstream on `main` and documented in FUTURE_IMPROVEMENTS.md; against a patched
build it is 196.5 MiB/s.

## 3.0.0

See the git history.
