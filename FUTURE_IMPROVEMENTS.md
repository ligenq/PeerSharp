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

## No tracker ever learns our IPv6 address

**Observed.** A dual-stack run of the Ubuntu desktop torrent announced only to
`https://torrent.ubuntu.com/announce`. `https://ipv6.torrent.ubuntu.com/announce` sits in the second
BEP 12 tier, and a tier is only tried when the one above it fails, so in a healthy run it received no
announce at all until the `stopped` event at shutdown. That single working announce goes out over
IPv4, so the address the tracker records for us is our IPv4 address and nothing else. IPv6-only peers
in that swarm therefore cannot be told about us — we can dial them, they cannot dial us.

**Why the obvious fix is not the fix.** BEP 7's `ipv6` announce parameter is exactly what this looks
like it wants, and it was tried. It is discouraged by the BEP itself: it lets a client claim any
address, and it defeats a tracker proxy by disclosing the address the proxy exists to hide. A tracker
following the BEP ignores it when the announce arrives from a global source address, which is every
case that matters here, so it buys nothing in exchange for the disclosure. It has been removed again.

**What would settle it.** What other clients do is announce once per listening address with the HTTP
connection bound to that address, letting each tracker infer the family from the connection. That
means a per-family announce loop in `TrackerManager` and a per-family `HttpClient` (or `SocketsHttpHandler`
with a bound `ConnectCallback`) in `HttpTracker` - both are real work, and the payoff is confined to
peers that have no IPv4 at all.

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

## Every socket binds to every interface, so a VPN cannot be enforced

**Observed.** Nothing in the engine can be told which local address to use, and every path defaults to
all of them. Inbound: `PortListener` asks for `IPAddress.Any` (`PortListener.cs:63`), which
`ITcpListener.Create` turns into a dual-mode `IPv6Any` bind (`ITcpListener.cs:27-34`); the shared UDP
socket binds `IPv6Any` or, in the IPv4 path, `new UdpClient(port)` which is `Any` by another spelling
(`IUdpSocket.cs:91-99`); LSD binds `Any` and `IPv6Any` outright (`LsdManager.cs:122,139`). Outbound is
the half that matters and is worse: `PeerCommunication` constructs a bare `new TcpClient()` and calls
`ConnectAsync` (`PeerCommunication.cs:773-775`), so the operating system picks the route.

**Why it matters.** A consumer - Peerfluence - wants a VPN kill switch, which is the single most
requested privacy feature for a torrent client, and it cannot be built on top of this engine. Binding
to the tunnel adapter is what makes the guarantee: if the tunnel drops, the bind fails and traffic
stops, rather than the operating system quietly re-routing peer connections and tracker announces out
of the real interface. A proxy is not a substitute - it covers what is sent through it, not what the
routing table does when it disappears. This is the same tunnel the interop baseline at the end of this
file was measured through, which is how the gap came up.

**Why it was left.** No measurement, and this entry is the odd one out in that respect: it is a missing
capability rather than a known cost. It was not built because nothing inside the engine needed it -
the listeners work, and a default route is the right default for a library that has not been told
otherwise.

**What would settle it.** A bind address on `Configuration`, threaded to four places, three of which
already have the seam:

- Inbound TCP and UDP go through `ITcpListenerFactory` and `IUdpSocketFactory`, so they take an address
  instead of assuming one. Mechanical.
- UDP trackers already take `IUdpSocketFactory`, so they follow from the same change
  (`UdpTracker.cs:87`).
- HTTP trackers go through `IHttpClientFactory` (`HttpTracker.cs:16,526-542`), which would need a
  `SocketsHttpHandler` whose `ConnectCallback` binds before connecting. Easy to forget, and forgetting
  it leaks the real address to every HTTP tracker while every peer connection looks correctly bound.
- Outbound peer connections are the one place with no seam: `PeerCommunication` builds its own
  `TcpClient`. It would have to construct it over a local `IPEndPoint`, which also means the uTP path
  and the shared UDP socket agree on the same address.

**The part to get right is the failure behaviour.** A bind to an address that has gone away throws,
and the correct response is to fail the connection, not to retry unbound. Anything that falls back to
`Any` on error turns the feature into a guarantee that silently is not one, which is worse than not
offering it. A test that binds to a loopback alias, removes it, and asserts that connections fail
rather than succeed by another route would be the thing that proves it.

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

**Caveat on all of these.** One machine, one version of each client, loopback, and a Mullvad tunnel up
throughout - the interop tests record the machine's interfaces at the start of each run for exactly
that reason. Loopback RTT is far below what any network-tuned heuristic expects, so the absolute
figures are not a benchmark. The comparison across counterparties is the result.
