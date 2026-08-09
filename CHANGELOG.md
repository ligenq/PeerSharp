# Changelog

Notable changes per release. Entries describe what a consumer of the library would notice; the commit
history has the reasoning and the measurements behind each one.

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
