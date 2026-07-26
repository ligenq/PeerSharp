<p align="center">
  <img src="src/PeerSharp/application-icon.png" alt="PeerSharp" width="128" />
</p>

<h1 align="center">PeerSharp</h1>

<p align="center">
  <a href="https://www.nuget.org/packages/PeerSharp"><img src="https://img.shields.io/nuget/v/PeerSharp.svg" alt="NuGet Version" /></a>
  <a href="https://github.com/ligenq/PeerSharp/actions/workflows/ci.yml"><img src="https://github.com/ligenq/PeerSharp/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT" /></a>
</p>

PeerSharp is a high-performance, modern BitTorrent engine for .NET 10+.

## Key Features

- **Full BEP Support:** Implements 35 BitTorrent Extension Protocols (see [Supported BEPs](#supported-beps)).
- **Hybrid Networking:** Native support for both TCP and uTP (BEP 29) with automatic congestion control.
- **DHT & Peer Discovery:** Full Mainline DHT (BEP 5), Local Service Discovery (BEP 14), Peer Exchange (PEX), and UDP/HTTP Tracker support.
- **Torrent Discovery:** Crawl the DHT for the info-hashes it knows about (BEP 51) and resolve them to names via BEP 9 — the building block for a search index.
- **Magnet Links:** Fast metadata exchange (BEP 9) allowing torrent starts from magnet links alone, with metadata-only fetch for previewing the file list before downloading, and metadata export for caching.
- **Self-Updating Torrents:** Mutable DHT records (BEP 44) and `xs=urn:btpk:` magnet links (BEP 46), so a publisher can release a new version under the same link and subscribers follow it automatically.
- **BitTorrent v2 & Hybrid Torrents:** Parse, create, announce, and verify v2/hybrid torrents with BEP 52 file trees, piece layers, and Merkle proofs.
- **Streaming Engine:** Integrated HTTP streaming server for real-time media playback while downloading.
- **Protocol Encryption:** MSE (Message Stream Encryption) with configurable enforcement modes.
- **NAT Traversal:** UPnP, NAT-PMP, and Holepunch (BEP 55) for connectivity behind NATs.
- **Bandwidth Control:** Per-torrent and global upload/download/disk I/O rate limiting.
- **Proxy Support:** SOCKS5 and HTTP proxy support with authentication.
- **IP Blocklist & GeoIP:** Block peers by IP range or country.
- **Optimized I/O:** Zero-copy Bencoding, pooled buffers, block caching, and asynchronous disk I/O designed for high-throughput scenarios.
- **Enterprise-Grade Testing:** Rigorous validation using **Microsoft Coyote** for concurrency testing, architecture tests for design integrity, fuzzing for robustness, and [BenchmarkDotNet suites](benchmarks/PeerSharp.Benchmarks/README.md) covering the engine's hot paths.

## Getting Started

### Installation

```bash
dotnet add package PeerSharp --version 2.1.0
```

Requires .NET 10.0 or later.

### Basic Usage

```csharp
using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;

// Initialize the engine
var engine = ClientEngineFactory.Create();
await engine.InitializeAsync();

// Add a torrent
var torrentFile = TorrentFile.Load("my_file.torrent");
var options = new AddTorrentOptions("./downloads");
var torrent = await engine.AddTorrentAsync(torrentFile, options);

// Or add from a magnet link
var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:...");
var torrent2 = await engine.AddMagnetAsync(magnet, new AddTorrentOptions("./downloads"));
```

### Creating Torrents

```csharp
var created = await new TorrentFileBuilder()
    .WithName("release")
    .WithVersion(TorrentFileVersion.Hybrid) // V1, V2, or Hybrid
    .WithPieceLength(256 * 1024)
    .AddTracker("https://tracker.example/announce")
    .AddFileFromPath("release.iso")
    .AddFileFromPath("install.sh", "install.sh", TorrentFileAttributes.Executable) // BEP 47 attributes
    .AddSymlink("latest.iso", "release.iso") // BEP 47 symlink entry
    .WithPerFileSha1() // BEP 47 per-file sha1 digests
    .BuildAsync();
```

### Monitoring Progress

PeerSharp supports two models for monitoring: a polling-based alert queue and per-torrent event callbacks.

```csharp
// Option 1: Polling alerts
await foreach (var alert in engine.Alerts.GetAlertsAsync())
{
    Console.WriteLine(alert);
}

// Option 2: Per-torrent event callbacks via builder
var events = new TorrentEventsBuilder()
    .OnProgressChanged((torrent, progress) =>
        Console.WriteLine($"Progress: {progress}"))
    .OnFinished((torrent, selectedOnly) =>
        Console.WriteLine($"Finished: {torrent}"))
    .Build();

var options = new AddTorrentOptions("./downloads") { Events = events };
```

### Previewing Magnet Links Before Downloading

For .torrent files the file list is available up front, so users can deselect files before
the download starts. Magnet links need their metadata fetched from the swarm first — two
APIs support that without downloading any file data:

```csharp
// Option 1: Fetch only the metadata and get a TorrentFile back.
// A transient torrent fetches the metadata and is removed again automatically.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var torrentFile = await engine.GetMagnetMetadataAsync(magnet, cts.Token);

for (int i = 0; i < torrentFile.FileCount; i++)
{
    Console.WriteLine($"{torrentFile.GetFile(i).Path} ({torrentFile.GetFile(i).Size} bytes)");
}

// Show your selection UI, then add it like a regular .torrent
var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions("./downloads"));

// Option 2: Add the magnet in preview mode - the torrent fetches its metadata and is
// then left stopped, giving a race-free window to adjust selections before starting.
var preview = await engine.AddMagnetAsync(magnet, new AddTorrentOptions("./downloads")
{
    StopAfterMetadata = true
});
await preview.WaitForMetadataAsync(cts.Token);   // stopped here, nothing downloaded yet
await preview.SetFilePriorityAsync(1, Priority.DoNotDownload);
await preview.StartAsync();
```

Fetched metadata can also be cached so the same magnet never needs a second metadata
download: persist `torrent.ExportTorrentFile().RawData` (or `torrentFile.RawData` from
option 1) and later re-add it via `TorrentFile.Parse(bytes)`.

### Self-Updating Torrents (BEP 46)

A publisher owns a key pair and stores a signed DHT record naming the current version. Subscribers
hold the public key instead of an info-hash, so a new release reaches everyone following the link
without a new link having to be distributed.

```csharp
// Publisher: create an identity once and persist the seed - it *is* the identity.
var key = TorrentPublisherKey.Create();
File.WriteAllBytes("publisher.seed", key.Seed.ToArray());

// Publish the current version. The version number is chosen automatically and
// compare-and-swapped, so a concurrent publish fails rather than silently overwriting.
var (nodes, version) = await engine.PublishSelfUpdatingTorrentAsync(key, created.InfoHash);

// Hand this link out once; it keeps working across releases.
Console.WriteLine(key.ToMagnetLink());

// Later, release a new version under the same identity.
await engine.PublishSelfUpdatingTorrentAsync(key, rebuilt.InfoHash);
```

```csharp
// Subscriber: resolve the link, then add the info-hash it names.
var magnet = MagnetLink.Parse("magnet:?xs=urn:btpk:...");
if (magnet.IsSelfUpdating)
{
    var current = await engine.ResolveSelfUpdatingMagnetAsync(magnet);
    if (current is not null)
    {
        var torrent = await engine.AddMagnetAsync(
            MagnetLink.Parse($"magnet:?xt=urn:btih:{current.Value.InfoHash}"),
            new AddTorrentOptions("./downloads"));
    }
}
```

Notes:

- Records expire from the DHT after roughly two hours. The engine re-publishes everything it has
  published on a timer, so a publisher only needs to stay running.
- Swapping a running torrent to a new version is **not** automatic. Poll
  `ResolveSelfUpdatingMagnetAsync` and compare `Version` against the one you already have; what to
  do with partially downloaded data from the previous version is an application decision.
- One identity can publish several torrents by passing a salt to both `ToMagnetLink` and
  `PublishSelfUpdatingTorrentAsync`.
- Interoperability is verified two ways: byte-for-byte against BEP 44's published test vectors,
  and against the live Mainline DHT. A survey of 105 walked nodes found 69% answer BEP 44 `get`
  and every one of those issues a write token; 7.6% return `204 Method Unknown`. See
  `tests/PeerSharp.Tests/Interop`, which is excluded from CI and gated on `PEERSHARP_INTEROP=1`.
- A fresh node's routing table commonly takes around 30 seconds to become usable. Publishing waits
  for six active lookup candidates (up to two minutes, or until its cancellation token is
  cancelled) before reading the current version and writing the update. Resolving remains a
  best-effort lookup and can return null while the table is still cold.

### Discovering Torrents in the DHT (BEP 51)

BEP 51 answers a question the rest of the library cannot: *what is out there*. Nodes will hand over a
sample of the info-hashes they hold peers for, so the DHT itself becomes enumerable — the basis for a
search index, or for surveying what is actually being shared.

Discovery yields bare info-hashes. Pairing it with the BEP 9 metadata fetch is what turns them into
names and file lists:

```csharp
var options = new DhtIndexerOptions { MaxInfoHashes = 500 };

await foreach (var found in engine.DiscoverInfoHashesAsync(options, cancellationToken))
{
    var link = MagnetLink.Parse($"magnet:?xt=urn:btih:{found.InfoHash}");

    // Bound the fetch: plenty of discovered hashes have no reachable peers left.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));

    try
    {
        var metadata = await engine.GetMagnetMetadataAsync(link, timeout.Token);
        Console.WriteLine($"{found.InfoHash}  {metadata.Name}  ({metadata.FileCount} file(s))");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // No peers answered in time; the hash is still a real discovery.
    }
}
```

Notes:

- A result is one node's claim to hold peers for a hash. It is not evidence the torrent exists, is
  reachable, or is anything in particular.
- The crawl has no natural end. It stops at `MaxInfoHashes`, when you `break`, or when the token is
  cancelled — cancelling throws `OperationCanceledException`, as cancelling any `IAsyncEnumerable`
  does. `MaxInfoHashes` also bounds memory, because suppressing duplicates means remembering every
  hash already returned.
- Each node's requested requery interval is honoured, floored by
  `DhtIndexerOptions.MinNodeRequeryInterval`. Live nodes really do ask for `0`, so the floor is what
  stops a crawl hammering them. While every known node is inside its interval, the crawl waits rather
  than finishing.
- Nodes without BEP 51 support are still queried with `find_node` so the frontier keeps growing. This
  matters more than it sounds: the bootstrap routers a fresh crawl necessarily starts from do not
  implement BEP 51, and without the fallback a crawl strands itself among them.
- The responder side is on by default and can be turned off with
  `Settings.Dht.AnswerInfoHashSampling = false`, which replies `204 Method Unknown` like any
  pre-BEP 51 node. It discloses nothing new — the same hashes are obtainable by asking us `get_peers`
  — but indexing is a choice an operator may want to opt out of. The subset offered is stable for the
  interval advertised and rotates afterwards, and the reported interval counts down to that rotation.
- Interoperability is verified against the live Mainline DHT: of 55 walked nodes, 87% answered
  `sample_infohashes` (notably better than the 69% BEP 44 `get` support measured above), 13 had
  hashes to give, and observed intervals spanned the full permitted 0–21600s range. See
  `tests/PeerSharp.Tests/Interop`, excluded from CI and gated on `PEERSHARP_INTEROP=1`.

### Streaming

```csharp
// Open a seekable stream for media playback
var stream = await torrent.OpenStreamAsync(fileIndex: 0);
```

## WebTorrent

PeerSharp.WebTorrent is an optional extension package that adds peer support over WebRTC data channels. Install it only in applications that need browser/WebTorrent interop; the core `PeerSharp` package has no dependency on RtcForge or WebRTC.

```bash
dotnet add package PeerSharp.WebTorrent --version 2.1.0
```

```csharp
using PeerSharp.Config;
using PeerSharp.WebTorrent;
using PeerSharp.WebTorrent.Configuration;

var addOptions = new AddTorrentOptions("./downloads")
{
    StartImmediately = false
};
var torrent = await engine.AddTorrentAsync(torrentFile, addOptions);

torrent.UseWebTorrent(new WebTorrentSessionOptions
{
    OffersPerTracker = 5,
    AdditionalTrackers = new[]
    {
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.webtorrent.dev"
    }
}, loggerFactory);

await torrent.StartAsync();
```

Notes for production use:

- WebTorrent discovery requires `ws://` or `wss://` trackers. UDP and HTTP trackers do not participate in WebTorrent signaling.
- The default ICE configuration is STUN-only. That is often enough for open networks and some home NATs, but not for symmetric-NAT or relay-required environments. For reliable browser-style connectivity you should supply TURN servers in `WebTorrentSessionOptions.IceServers`.
- There is a demo harness at [samples/PeerSharp.WebTorrent.Demo/Program.cs](samples/PeerSharp.WebTorrent.Demo/Program.cs) for controlled interop and soak testing.
- The `PeerSharp.WebTorrent` logger category emits reconnect, pending-peer expiry, and signaling lifecycle information. For rollout, capture this category at `Information` or `Debug`.

Recommended validation before broad rollout:

1. Verify announce and peer negotiation against at least one browser WebTorrent client and a couple of real WebSocket trackers.
2. Run long-lived churn tests with forced tracker disconnects and failed negotiations to confirm pending peers remain bounded.
3. Test at least one TURN-backed path in addition to STUN-only connectivity.

## Supported BEPs

PeerSharp aims for high compatibility with the BitTorrent ecosystem:

| BEP | Title | Status |
|-----|-------|--------|
| 3   | The BitTorrent Protocol Specification | Supported |
| 5   | DHT Protocol | Supported |
| 6   | Fast Extension | Supported |
| 7   | IPv6 Tracker Extension | Supported |
| 9   | Extension for Peers to Send Metadata Files | Supported |
| 10  | Extension Protocol | Supported |
| 11  | Peer Exchange (PEX) | Supported |
| 12  | Multitracker Metadata Extension | Supported |
| 14  | Local Service Discovery | Supported |
| 15  | UDP Tracker Protocol | Supported |
| 16  | Superseeding | Supported |
| 19  | WebSeed - HTTP/FTP Seeding (GetRight style) | Supported |
| 20  | Peer ID Conventions | Supported |
| 21  | Extension for Partial Seeds | Supported, `upload_only` plus `event=paused` while a partial seed |
| 23  | Tracker Returns Compact Peer Lists | Supported |
| 24  | Tracker Returns External IP | Supported, counted as a vote towards the BEP 42 node ID |
| 27  | Private Torrents | Supported |
| 29  | uTorrent Transport Protocol (uTP) | Supported |
| 30  | Merkle Hash Torrent Extension | Supported |
| 31  | Tracker Failure Retry Extension | Supported, `retry in` overrides our own backoff; `never` disables the tracker for the session |
| 32  | IPv6 Extension for DHT | Supported |
| 33  | DHT Scrape | Supported |
| 40  | Canonical Peer Priority | Supported |
| 41  | UDP Tracker Protocol Extensions | Supported, carries the URL path and query so passkeys survive a UDP announce |
| 42  | DHT Security Extension | Supported |
| 43  | Read-only DHT Nodes | Supported, honoured inbound and settable via `Settings.Dht.ReadOnly` |
| 44  | Storing Arbitrary Data in the DHT | Supported, immutable and mutable items, as both client and storage node |
| 47  | Padding Files and Extended File Attributes | Supported, including padding-file creation and download skipping |
| 46  | Updating Torrents Via DHT Mutable Items | Supported, including `xs=urn:btpk:` magnet links |
| 48  | Tracker Protocol Extension: Scrape | Supported |
| 51  | DHT Infohash Indexing | Supported, both as responder and as crawler |
| 52  | The BitTorrent Protocol Specification v2 | Supported |
| 53  | Magnet URI Extension - Select Specific File Indices for Download | Supported |
| 54  | The lt_donthave Extension | Supported, sent when a piece turns out to be unreadable |
| 55  | Holepunch Extension | Supported |

### Deliberate non-goals

Every other BEP on bittorrent.org is an omission on purpose, not a gap. Those marked *deferred* are the
ones the BEP editors themselves record as no longer progressing toward standardization; the rest are
live drafts we chose not to implement.

| BEP | Title | Why not |
|-----|-------|---------|
| 8   | Tracker Peer Obfuscation *(deferred)* | RC4 keyed on the info-hash, a value every participant already holds — the BEP is explicit that it is obfuscation, not security. An `https://` announce URL does the job properly and already works, with MSE covering peer connections. Also needs tracker-side support that essentially nothing deploys |
| 17  | HTTP Seeding (Hoffman-style) | Needs a server-side script speaking its own query format; effectively nothing deploys it, and every web seed in the wild is reachable through BEP 19 above |
| 18  | Search Engine Specification *(deferred)* | An OpenSearch-subset XML file describing a search provider for a client's search box. UI scope, not engine scope — and a consumer can parse `.btsearch` with `System.Xml` in a few lines |
| 22  | Local Tracker Discovery *(deferred)* | Requires ISPs to publish SRV records under their reverse-DNS domains, which never happened. Were it working, it would announce to a tracker chosen automatically by the network rather than by the user, so it would belong behind an explicit opt-in regardless |
| 26  | Zeroconf Peer Advertising *(deferred)* | The same LAN discovery job as BEP 14 above, but delegated to a Bonjour/Avahi daemon — a platform dependency where the current LSD is self-contained multicast. Its browsable registry also lets a device enumerate what a host shares retroactively, where LSD only reaches whoever was listening |
| 28  | Tracker Exchange *(deferred)* | The closest call of these. It is genuinely deployed (libtorrent) and would be cheap here — BEP 10, BEP 12 tiers, `MagnetTrackerMerger` and the circuit breaker are all in place. But a peer-supplied announce URL lets a stranger make us disclose our IP and info-hash to a server of their choosing, and the BEP resolves that with nothing firmer than "a certain amount of suspicion". BEP 27 also excludes private torrents, which is the population that most wants it. Revisit only as opt-in, off by default, capped, and never propagating a tracker that has not worked for us |
| 34  | DNS Tracker Preferences | Requires tracker operators to publish DNS records; essentially none do |
| 35  | Torrent Signing | Never deployed. BEP 46 covers the "is this really from the publisher" need with Ed25519 |
| 36  | Torrent RSS Feeds | Not a wire protocol — an application fetching XML. Belongs above a library, not inside one |
| 38  | Finding Local Data Via Torrent File Hints | Not ruled out. Worth it only if publishing related torrents is a use case; the matching half needs a local cross-torrent file search |
| 39  | Updating Torrents Via Feed URL | The feed-based predecessor to BEP 46, which is implemented instead |
| 45  | Multiple-address Operation for the DHT | IPv4/IPv6 is already covered by BEP 32; this only pays off on genuinely multi-homed hosts |
| 49  | Distributed Torrent Feeds | Buildable on the BEP 44/46 foundation here, but near-zero deployment means defining an ecosystem rather than joining one |
| 50  | Publish/Subscribe Protocol | As above, and less finished |

BEPs 0, 1, 2 and 1000 are process documents. BEP 4 is a number registry rather than a feature, and the
reserved bits and extension message ids used here follow it.

### A note on BEP 41 and packet size

BEP 41 makes UDP announces longer than the 98 bytes of BEP 15 alone. That is the extension point the
BEP defines, and implementations read fixed offsets, so trackers without support ignore the trailing
bytes. It is on by default because the alternative is a silent failure — a passkey that never arrives,
a working socket and no peers. Set `Settings.SendUdpTrackerUrlData = false` if a particular tracker
rejects the longer packet.

## Architecture

PeerSharp is designed with a modular, interface-driven architecture:

- **ClientEngine:** The central orchestrator managing multiple torrent sessions.
- **PiecePicker:** Advanced logic for piece selection (rarest-first, sequential, streaming modes).
- **Storage:** Abstracted disk I/O layer with sparse file support, block caching, and file handle pooling.
- **BEncoding:** A high-performance, allocation-aware parser and writer for the BitTorrent data format.
- **Alert System:** A centralized event bus with both polling and callback models for real-time monitoring.
- **NetworkManager:** TCP and uTP connection handling with protocol encryption.
- **DhtManager:** Full Kademlia-style distributed hash table with security extensions.

## License

Distributed under the MIT License. See `LICENSE` for more information.
