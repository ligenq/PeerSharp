# Future Improvements

Work that was identified, measured, and deliberately not done. Everything here is a known gap rather
than a suspicion: each entry says what was observed, why it was left, and what would settle it.

This is not a roadmap and nothing here is a defect in the sense of producing wrong results. The README
describes what PeerSharp does; this describes where it is known to be leaving something on the table.

**Before acting on any of these, measure first.** Several entries below exist because an earlier
assumption about where time was going turned out to be wrong when a log was actually read — the
metadata rebuild cost was attributed to peer teardown for a while, and it was the tracker announce.

---

## Peer connections are discarded when a magnet's metadata arrives

**Observed.** When metadata completes, `Torrent.ReinitializeAfterMetadataAsync` stops the torrent,
rebuilds its internals and starts again. `Initialize()` constructs a new `PeerManager`, so every
connection established while waiting for metadata is closed. A live magnet had roughly ten handshaked
peers at that moment, all discarded and re-dialled.

**Why it was left.** The connections cannot simply be kept. Before metadata exists the piece count is
zero, so `PeerPieces` is empty and incoming `Have` messages are dropped
(`PeerManager.MessageReceivedAsync`, the `!_torrent.HasMetadata` branch). A bitfield is only ever sent
once, immediately after the handshake, and there is no protocol message to ask for it again — so a
preserved peer would be one whose contents we can never learn. Keeping it would be worse than
reconnecting.

**What would settle it.** libtorrent solves this by storing the raw bitfield before the picker exists
and replaying it afterwards (`peer_connection::incoming_bitfield`, the `!t->ready_for_connections()`
branch: `m_have_piece = bits`). Doing the same means retaining the bytes in `PeerCommunication`,
re-basing `PeerPieces` once the piece count is known, and having the rebuild adopt the existing
connections rather than construct a fresh `PeerManager`. That last part is the risky half.

**Measure first.** The rebuild's *latency* cost has already been removed — it was the tracker stop
announce, not the teardown, and the peers close in about fifteen milliseconds. What remains is a
throughput question: whether re-dialling delays the first block. A log showing the gap between metadata
completing and the first block arriving would say whether this is worth the work.

---

## Metadata requests are piece-driven rather than peer-driven

**Observed.** `MetadataDownload` drives requests from a timer over the pieces it still needs. libtorrent
drives them from the peers: `maybe_send_request()` runs whenever a peer becomes available, each peer
independently picks the least-requested piece (`std::min_element` over `num_requests`) and may hold two
outstanding, throttled only by not re-asking for the same piece within three seconds.

**Why it matters.** With a hundred metadata-capable peers, libtorrent can have hundreds of requests in
flight for a payload measured in kilobytes. PeerSharp asks three peers per piece and waits for a
timeout. The difference is latency at the only moment when a magnet has nothing else to do.

**Why it was left.** The current model is now fast enough that the remaining gap is unmeasured. Moving
to peer-driven requests means restructuring `_pendingRequests` from one entry per piece to per-peer
tracking, which touches roughly every method in the class.

---

## A newly-connected peer usually gets nothing to do

**Observed.** `MetadataDownload.PeerConnected` does route every case into the request path, but that
path is `FillMissingRequests`, which only requests pieces that are not already pending. The request
pipeline is 8 and metadata is typically 2-8 pieces, so by the time a second peer arrives every piece
already has an outstanding request and the new peer is added to the pool with nothing asked of it. It
then waits for a timeout before being considered.

**Why it matters.** The peers that arrive early are the ones most likely to answer quickly, and they are
precisely the ones left idle. libtorrent has no such gap because its requests are per-peer: a new peer
picks the *least-requested* piece whether or not that piece is already outstanding, so it always has
something to do.

**Why it was left.** Fixing it properly is the peer-driven restructure above. A narrower version -
letting a newly-arrived peer duplicate an already-pending piece - would capture much of the benefit for
much less work, and is the first thing to try.

---

## End game duplicate requests are unbounded

**Observed.** `StandardBlockRequestStrategy` caps concurrent requests per block at two.
`EndGameBlockRequestStrategy` has no cap by design — broad duplication is the whole strategy when only
a few blocks remain.

**Why it was left.** Deliberate, and matching convention. But the cost was never measured: a cancel sent
when the first copy arrives cannot overtake data already on the wire, and end game is exactly when the
most peers are asked at once. Before the cap existed, duplicate delivery across a whole session was
10.6% of everything downloaded.

**What would settle it.** Count blocks received that were already held, during end game specifically. If
it is a meaningful fraction, a cap of three or four would still finish the torrent.

---

## Consumers cannot observe a peer's final transfer totals

**Observed.** `AlertId` has no peer-level entries — the alerts are all torrent, metadata or config
scoped. Per-peer byte counts exist only on `PeerInfo`, which is a snapshot of *currently connected*
peers.

**Why it matters.** Anything a peer transferred is unobservable if that peer disconnects between polls.
This caused a real false alarm: a diagnostic reported that no Transmission peer had ever received a
byte, while the engine had in fact sent one 512 KiB — the peer unchoked, transferred and hung up inside
a single two-second interval. The consumer has no way to see that; only the engine's own running total
revealed it.

**What would settle it.** A `PeerDisconnected` alert carrying the endpoint, client name and final
byte counts. Additive to `AlertId`, so it does not disturb existing consumers.

---

## Adaptive connection timeouts are keyed by address, not endpoint

**Observed.** `AdaptiveTimeout.GetEndpointKey` returns `endpoint.Address.ToString()`, deliberately
ignoring the port, on the reasoning that one host has one latency.

**Why it may not hold.** Peers behind carrier-grade NAT or a seedbox share an address while being
entirely different peers, and a busy host can have many ports with very different behaviour. Success
statistics from one are applied to all of them.

**What would settle it.** Compare connect success rates keyed by address against keyed by endpoint over
a soak run. Cheap to measure, since both keys can be tracked at once.

---

## Nothing distinguishes a poisoned swarm from a client fault

**Observed.** One magnet never acquired metadata across a full session while three siblings in the same
process completed in eight to sixteen seconds. Its six metadata-capable peers all advertised a correct
and consistent size, were asked roughly 110 times over 111 seconds, and answered nothing. Its
connections also failed before the BitTorrent handshake at 3.5 times the rate of the others.

**Why it was left.** No client-side change helps against peers that complete a handshake and then lie —
libtorrent would fare no better, since its `m_request_limit` backoff only triggers on an explicit
`dont_have` or a failed hash check, never on silence.

**What would help.** The engine knows "six peers claim to hold metadata, 110 requests sent, none
answered" and surfaces none of it. A warning after a metadata download has been asking healthy-looking
peers for some time with nothing to show would turn a silent stall into a diagnosis. This is reporting,
not a fix.

---

## Per-peer transport and encryption preferences are not persisted

**Observed.** `PeerHistory` learns whether a peer speaks uTP (`UtpSupported`) and whether it accepts an
encrypted handshake (`OfferEncryptionNext`). Neither survives a restart — the cache is in memory only.

**Why it matters.** Every session relearns the same facts about the same peers, and relearning
`OfferEncryptionNext` costs a failed connection each time. Session persistence already stores DHT state
and resume data, so the machinery exists.

**Why it was left.** Unmeasured. The alternation costs at most one extra attempt per peer per session,
which may be lost in the noise of ordinary churn.

---

## `MetadataRequestRedundancy` is a constant, not a setting

**Observed.** How many peers are asked for the same metadata piece is a private `const` in
`MetadataDownload`, while its neighbours (`MetadataRequestPipeline`, `MetadataRequestTimeoutSeconds`,
`MetadataMaxRequestAttempts`) are all settings.

**Why it was left.** It is a protocol-tuning detail rather than something a consumer should reach for,
and adding public API is not free. Worth revisiting only if someone has a reason to change it.

---

## BEP 38 and BEP 45

Identified as conditional during the BEP audit and never implemented.

- **BEP 38** (`similar` / `collections`) — lets a torrent point at content it shares data with, so a
  client can seed from files it already has. Valuable only to consumers managing related torrents.
- **BEP 45** (multiple-address DHT announce) — matters for hosts with several public addresses.

Neither has a consumer asking for it.

---

## A metadata fetch cannot report its progress

**Observed.** `ClientEngine.GetMagnetMetadataAsync` now runs its torrent transiently: no lifecycle
alerts, no session entry, no queue participation, no claim on the info hash, and absent from
`GetTorrents`. That was the fix for a consumer seeing preview torrents appear in its download list
and announce themselves as ready. It also removed the only channel through which the fetch's
progress was visible, because `MetadataProgressChanged` came from the same silenced torrent.

**Why it matters.** Fetching metadata for a cold magnet is the one moment a user is asked to wait on
something with no feedback. The stream previously carried progress, but ambiguously - a consumer
could not tell a preview's metadata progress from a real download's. Now it carries nothing, which
is the more honest of the two and the less useful. A caller has only "the task has not completed".

**Why it was left.** The ambiguity was the reported defect and the silence resolves it. Adding
progress back needs a channel scoped to the operation rather than the engine, which is a small API
design question rather than a mechanical change: an `IProgress<MetadataProgress>` parameter is the
obvious shape, but an operation object returning both the task and its progress composes better if
the fetch ever grows other observable state.

**What would settle it.** A consumer that actually wants the indicator. Peerfluence shows an
indeterminate "fetching metadata" state and has not asked for more, so the shape of the API should
follow a real requirement rather than a guess at one.

---

## A magnet's peers are lost when its metadata is added as a torrent file

**Observed.** The intended flow for `GetMagnetMetadataAsync` is to show the returned `TorrentFile` to
the user and then add *that* rather than the magnet, so the metadata download is not repeated. The
exported file carries the magnet's trackers — `AddMagnetInternal` seeds `AnnounceList` from
`MagnetLink.Trackers` and `TorrentFileSerializer` writes them back out — but not its peers.
`MagnetLink.Peers` (BEP 9 `x.pe`) is applied inside `AddMagnetCoreAsync`, and `AddTorrentOptions` has
no equivalent field, so the second add starts with no peer hints. `SelectOnlyFileIndices` (BEP 53) is
lost the same way, though a caller showing a file-selection UI supersedes it anyway.

**Why it matters.** `x.pe` exists to skip discovery latency on a fresh magnet. A caller following the
documented preview flow silently gives that up, and gives it up precisely in the case the flow is for:
the user has just waited for metadata and is now waiting again for peers.

**Why it was left.** Unmeasured. `x.pe` is uncommon in the wild, and the peers a magnet carries are
often stale by the time a user has read a file list and clicked Add — the discarded hints may be worth
nothing. The metadata fetch also had live peers for this swarm moments earlier, which is a larger
prize than the magnet's static list and a separate question from this one.

**What would settle it.** A run comparing time-to-first-block for a magnet with `x.pe` added directly
against the same magnet previewed and then added as a `TorrentFile`. If the gap is real, an
`AdditionalPeers` field on `AddTorrentOptions` matching the existing `AdditionalTrackers` is the small
version of the fix.

---

## BEP 51 duplicate suppression hides discovery confidence

**Observed.** `DhtInfoHashCrawler` keeps a `HashSet<InfoHash>` and yields only the first sighting of
each hash. `DiscoveredInfoHash.Source` therefore identifies one reporting node, but the consumer can
never learn that later, independent nodes sampled the same hash. `SourceTotalInfoHashes` is the size
of that node's whole sample population, not a count for the discovered torrent.

**Why it matters.** A consumer building a discovery surface cannot fetch metadata for every sampled
hash cheaply. Independent sightings would provide a useful prioritisation signal: resolve hashes seen
from several nodes before one-off results. It is not proof of popularity, availability or safety—a
malicious operator can run several nodes—but discarding the signal entirely leaves consumers with no
better ordering than arrival time.

**Why it was left.** Distinct output is simple, bounds the public stream by `MaxInfoHashes`, and avoids
making duplicate handling every consumer's problem. Changing the existing semantics would also make
the limit ambiguous: unique hashes and emitted sightings are different quantities.

**What would settle it.** Preserve the distinct stream as the default, but optionally expose repeat
sightings or aggregated updates. This could be a `ReturnDuplicateSightings` option, a separate
sampling API, or a result carrying a source count that can be updated through another event stream.
Keep the unique-hash memory limit separate from any emitted-sighting limit, and document that source
count is an untrusted ranking heuristic only.

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

## Leeching from Transmission runs at a fraction of every other path

**Observed.** Four measurements, same machine, same loopback, same 256 MiB payload of random bytes,
same MSE-encrypted connection, and in every Transmission case PeerSharp is the side that dials, so
the TCP direction and the encryption handshake are held constant:

| Path | Rate |
| --- | --- |
| PeerSharp seeds, PeerSharp leeches, plaintext | 108 MiB/s |
| PeerSharp seeds, PeerSharp leeches, encrypted | 234 MiB/s |
| PeerSharp seeds, **Transmission leeches** | 7.7 MiB/s |
| **Transmission seeds**, PeerSharp leeches | 0.3 MiB/s |

**What that isolates.** The ceiling does not follow the data: reversing which side sends made it
twenty-five times *worse*, not better. So this is not our send path, which serves 234 MiB/s to a
PeerSharp leecher and 7.7 to Transmission. Nor is it our receive path in general, because the
control arm has PeerSharp leeching too, at 234 MiB/s. Nor is it MSE, which the encrypted control
already exonerated by being the faster of the two PeerSharp runs. What is left is an interaction:
something Transmission does while seeding that we handle badly while receiving.

**Why it matters more than the seeding number.** Downloading is the case that matters, Transmission
is one of the three implementations worth interoperating with, and 0.3 MiB/s is not a degradation -
it is unusable. A real swarm mixes clients, so this would be masked by whichever peers are not
Transmission rather than being obvious.

**What was seen but not explained.** Progress arrives in steps of roughly one to two MiB separated
by stalls of several seconds, rather than flowing. Early in a run the connected-peer count
alternates between one and zero on a several-second cycle, which settles later. Both point at
something cyclic - a choke/unchoke rotation, or requests timing out and being reissued - rather than
at a bandwidth limit. Our pipelining is adaptive (`InitialPipelineDepth` 16, `MaxRequestsPerPeer`
128) and works against a PeerSharp seed, so the depth alone does not explain it.

**What would settle it.** The harness is `TransmissionInteropTests.Leeching_FromTransmission_ReceivesTheWholeFile`,
which reproduces it in about four minutes at 64 MiB. Transmission's peer log at `message-level` 4
and our own request/timeout tracing on the same run, aligned on the stalls, should say whether we
stop asking or it stops answering. Do that before touching the request strategy: the pipelining
entries above are plausible-looking neighbours and would be easy to blame wrongly.

**Caveat on the numbers.** One machine, one Transmission version (4.1.3), loopback, and one run per
arm. Loopback RTT is far below anything a heuristic tuned for real networks expects, so the absolute
figures are not a benchmark. The ordering across the four arms is the result, not the values.
