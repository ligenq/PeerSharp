# Testing PeerSharp

How to run the suites that need more than `dotnet test`, and how to read what they report.
The unit and integration tests need no setup and run on every build; everything below does not.

| Suite | Gate | Where |
|---|---|---|
| Unit, integration, architecture, property-based | none - runs on every build | `tests/PeerSharp.Tests` |
| Coverage-guided fuzzing | AFL++ and a corpus | [PeerSharp.Fuzz](PeerSharp.Fuzz/README.md) |
| Local counterpart-client interop | real client binaries; nightly in CI | `tests/PeerSharp.Tests/Interop` |
| Live DHT probes | `PEERSHARP_INTEROP=1` | `tests/PeerSharp.Tests/Interop` |
| Real-swarm soak | `PEERSHARP_SOAK=1` plus content you choose | `tests/PeerSharp.Tests/Interop` |

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
runs nightly in CI (`.github/workflows/interop.yml`), not on pull requests. It needs real client
binaries and real transfers, which is too slow and too dependent on an apt mirror to sit in front of
every change, but leaving it entirely to a human meant an interop regression could sit unnoticed for
as long as nobody happened to run it. A test whose counterpart client is not installed skips rather
than fails, so read the skip count: a run where everything skipped proves nothing.

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
