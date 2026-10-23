<p align="center">
  <img src="https://raw.githubusercontent.com/ligenq/PeerSharp/main/src/PeerSharp/application-icon.png" alt="PeerSharp" width="128" />
</p>

<h1 align="center">PeerSharp</h1>

<p align="center">
  <a href="https://www.nuget.org/packages/PeerSharp"><img src="https://img.shields.io/nuget/v/PeerSharp.svg" alt="NuGet Version" /></a>
  <a href="https://github.com/ligenq/PeerSharp/actions/workflows/ci.yml"><img src="https://github.com/ligenq/PeerSharp/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT" /></a>
</p>

PeerSharp is a high-performance, modern BitTorrent engine for .NET 10+.

See [Application integration APIs](CONSUMER_API.md) for torrent identity, magnet creation,
polling statistics, and proxy capability reporting.

## Key Features

- **BEP Support:** Implements the protocols listed under [Supported BEPs](#supported-beps).
- **Hybrid Networking:** Native support for both TCP and uTP (BEP 29) with automatic congestion control.
- **DHT & Peer Discovery:** Full Mainline DHT (BEP 5), Local Service Discovery (BEP 14), Peer Exchange (PEX), and UDP/HTTP Tracker support.
- **Torrent Discovery:** Crawl the DHT for the info-hashes it knows about (BEP 51) and resolve them to names via BEP 9 — the building block for a search index.
- **Magnet Links:** Fast metadata exchange (BEP 9) allowing torrent starts from magnet links alone, with metadata-only fetch for previewing the file list before downloading, and metadata export for caching.
- **Self-Updating Torrent Links:** Publish and resolve mutable DHT records (BEP 44) and `xs=urn:btpk:` magnet links (BEP 46). Applications decide when to switch downloads to a new version.
- **BitTorrent v2 & Hybrid Torrents:** Parse, create, announce, and verify v2/hybrid torrents with BEP 52 file trees, piece layers, and Merkle proofs.
- **Streaming Engine:** Integrated HTTP streaming server for real-time media playback while downloading.
- **Protocol Encryption:** MSE with RC4 or negotiated plaintext payloads and configurable enforcement.
- **NAT Traversal:** UPnP, NAT-PMP, and Holepunch (BEP 55) for connectivity behind NATs.
- **Bandwidth Control:** Per-torrent and global upload/download/disk I/O rate limiting.
- **Proxy Support:** SOCKS5 and HTTP proxy support with authentication. Only SOCKS5 can carry UDP, so an HTTP proxy selected for that traffic makes DHT and uTP refuse to start and UDP tracker requests fail rather than send around it. SOCKS5 resolves UDP tracker names remotely. Proxied DHT bootstrap requires saved nodes or IP addresses in `Dht.BootstrapNodes`; hostname bootstrap is skipped to prevent local DNS leaks. WebTorrent sessions reject peer proxy configurations because the WebRTC adapter does not support proxy routing.
- **IP Blocklist & GeoIP:** Block peers by IP range and label connected peers by country.
- **One Error Model:** Everything the library reports as its own failure derives from `PeerSharpException`, so a malformed torrent, a refused tracker and an unwritable disk are told apart by type rather than by message.
- **Optimized I/O:** Allocation-aware Bencoding, pooled buffers, block caching, and asynchronous disk I/O.
- **Thoroughly Tested:** Property-based tests over the parsers, caches and schedulers, repeated concurrency stress over the shared-state paths, architecture tests for design integrity, fuzzing for robustness, byte-for-byte resume and interop checks against real clients, and [BenchmarkDotNet suites](https://github.com/ligenq/PeerSharp/blob/main/benchmarks/PeerSharp.Benchmarks/README.md) covering the engine's hot paths.

## Getting Started

### Installation

```bash
dotnet add package PeerSharp --version 5.2.0
```

Requires .NET 10.0 or later.

### Release integrity

Each GitHub release includes SPDX SBOMs for `PeerSharp` and `PeerSharp.WebTorrent` alongside the NuGet and symbol packages. Release packages have signed GitHub build-provenance and SBOM attestations. After downloading a package, verify its origin with the GitHub CLI:

```bash
gh attestation verify PeerSharp.5.2.0.nupkg --repo ligenq/PeerSharp
```

### Basic Usage

```csharp
using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;

// Initialize the engine
await using var engine = ClientEngineFactory.Create();
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

### Metrics

Alerts and events tell an application what just happened. For what is happening *now*, across a
process, the engine publishes to a `Meter` named `PeerSharp`:

```csharp
using PeerSharp.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(PeerSharpMetrics.MeterName));
```

| Instrument | Unit | Meaning |
|---|---|---|
| `peersharp.download.speed` | By/s | Aggregate download rate |
| `peersharp.upload.speed` | By/s | Aggregate upload rate |
| `peersharp.downloaded` | By | Lifetime total downloaded |
| `peersharp.uploaded` | By | Lifetime total uploaded |
| `peersharp.torrents` | {torrent} | Torrents being managed |
| `peersharp.torrents.active` | {torrent} | Downloading, checking or fetching metadata |
| `peersharp.peers.connected` | {peer} | Connected peers across all torrents |

Every instrument is observable: measurements are collected when a collector polls, with no separate
enable switch. The gauges read the same
aggregate `engine.GetStats()` returns, which describes the torrents present now. The two byte counters
deliberately do not: they cover the engine's whole life, including torrents since removed, because a
counter that falls when a torrent is removed reads to a metrics backend as a process restart.

Measurements are tagged `peersharp.engine.id`, and each engine's meter carries the engine itself as
`Meter.Scope`, so several engines in one process stay apart.

### Previewing Magnet Links Before Downloading

For .torrent files the file list is available up front, so users can deselect files before
the download starts. Magnet links need their metadata fetched from the swarm first — two
APIs support that without downloading any file data:

```csharp
// Option 1: Fetch only the metadata and get a TorrentFile back.
// The torrent that does the fetching is invisible: it never appears in GetTorrents(),
// emits no alerts, takes no session entry, and does not reserve the info hash - so
// previewing a magnet you already hold, or adding it mid-preview, both work.
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
- Tests cover BEP 44's published test vectors. Live Mainline DHT tests are opt-in with
  `PEERSHARP_INTEROP=1`; see [tests/README.md](tests/README.md).
- A fresh node's routing table may need time to become usable. Publishing waits
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
- Live sampling tests are opt-in with `PEERSHARP_INTEROP=1`; see
  [tests/README.md](tests/README.md). Availability depends on the nodes reached during a run.

### Managing a Running Torrent

Beyond start, stop and file selection:

```csharp
// Move the data somewhere else and carry on from there. SetDownloadPathAsync only repoints at a new
// directory - it leaves the data behind, which on the next start reads as a torrent that has nothing.
await torrent.StopAsync();
await torrent.MoveStorageAsync("/mnt/archive/downloads");

// Store one file under a different name, taking what has been downloaded with it. The torrent's own
// metadata is untouched, so the info hash does not change.
await torrent.RenameFileAsync(0, "movies/feature.mkv");

// Priorities below the level of a file: a media header, a range about to be read, an archive's index.
torrent.SetPiecePriority(0, Priority.High);
// ReadPieceAsync reads verified data; call it after piece 0 completes.
byte[] header = await torrent.ReadPieceAsync(0);

// Limits for this torrent alone, overriding the engine-wide settings.
torrent.MaxConnections = 30;
torrent.MaxUploadSlots = 4;

// Web seeds the torrent's metadata did not name - a mirror set up later, or a local HTTP copy.
torrent.WebSeeds.Add("https://mirror.example/releases/");

// Ask the trackers for swarm counts now, rather than waiting for the next scheduled scrape.
await torrent.Trackers.ScrapeAsync();
foreach (var tracker in torrent.Trackers.GetTrackers())
{
    Console.WriteLine($"{tracker.Url}: {tracker.SeedCount} seeds, {tracker.LeechCount} leechers");
}

// Seeding into an empty swarm: BEP 16 hands each peer one piece at a time so no two peers get the
// same one, which costs the seed far less upload to get a full copy out. Turn it off afterwards -
// against an established swarm it only throttles peers that could have downloaded freely.
torrent.SuperSeeding = true;
```

The engine can also be stopped as a whole - for a machine going to sleep, or a metered connection
coming up. `ResumeAsync` starts back exactly the torrents `PauseAsync` stopped, so a torrent the user
had already stopped by hand stays stopped:

```csharp
await engine.PauseAsync();
// ... engine, DHT node and listeners stay up; nothing transfers
await engine.ResumeAsync();
```

### Streaming

```csharp
// Open a seekable stream for media playback
var stream = await torrent.OpenStreamAsync(fileIndex: 0);
```

A stream fetches the pieces ahead of its read position first, along with the start and end of the
file where containers keep their indexes. Several streams can be open at once, which is what a player
seeking or fetching an index does; the torrent returns to its configured strategy when the last one
closes. How far ahead to fetch and how long to wait are in `settings.Streaming`.

To hand a file to a player, serve it over HTTP. By default the server listens on loopback for a player
on the same device. To stream to a Chromecast, a TV or another computer, bind it to this device's
address on that network; the URL then carries a generated access token.

```csharp
using var server = new HttpStreamServer(torrent, fileIndex: 0, new HttpStreamServerOptions
{
    BindAddress = IPAddress.Parse("192.168.1.20"),
});
server.Start();

// Give the player server.Url, and a cast receiver server.ContentType as well.
```

### MSE payload modes

`settings.Connection.Encryption` controls which connections are permitted:

- `Allow` supports ordinary plaintext connections and MSE with either RC4 or plaintext payloads.
- `Require` permits MSE with RC4-encrypted payloads only.
- `Refuse` uses ordinary plaintext connections and declines MSE.

The MSE handshake and its initial payload use RC4 in both negotiated modes. Afterward, payloads
follow the selected mode. With the default `PreferRc4 = true`, outgoing MSE offers RC4 only and
incoming MSE selects RC4 whenever the peer offers it. Set `settings.Connection.PreferRc4 = false`
under `Allow` to offer both outgoing methods and prefer plaintext on incoming handshakes. The
remote peer chooses among the outgoing offer. `PreferRc4` does not weaken `Require`, and `Allow`
can still fall back to an ordinary plaintext connection. These settings apply to new handshakes.

Incoming and outgoing policies can be set independently. A null override follows `Encryption`, so
existing configurations keep their behavior:

```csharp
settings.Connection.OutgoingEncryption = Encryption.Require;
settings.Connection.IncomingEncryption = Encryption.Allow;
```

### HTTP seeds, SSL torrents and I2P

BEP 17 `httpseeds` endpoints are supported alongside BEP 19 `url-list` file mirrors. They use
separate request formats: HTTP seeds receive the info hash, piece index and piece-relative ranges.
Server retry intervals are honored, and downloaded pieces undergo the normal hash verification.
`TorrentFile.HttpSeeds` exposes these endpoints; `WebSeeds` continues to expose BEP 19 file URLs.

SSL torrents containing an `info["ssl-cert"]` CA require mutually authenticated TLS on every peer
connection, over TCP or uTP. Supply a certificate with its private key when adding the torrent:

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509Certificate2.CreateFromPemFile("peer.pem", "peer-key.pem");
var torrent = await client.AddTorrentAsync(torrentFile,
    new AddTorrentOptions { SslCertificate = certificate });
```

The client copies and owns its certificate. Both peers must have a certificate issued by the
embedded CA, with a DNS subject alternative name matching the torrent name exactly, or `*`.
A common name is accepted when no DNS alternative name exists. Incoming TLS uses the info hash
in SNI to select the torrent; ordinary peer connections cannot bypass certificate authentication.
TLS connections use the existing peer listener ports. Certificates and private keys are not saved
in resume/session files: pass the credentials again when adding an SSL torrent after restart.
MSE policy applies to ordinary torrents; SSL torrents always use TLS.

For torrents with an I2P tracker, configure a running router's SAM bridge:

```csharp
settings.Connection.I2pSamHost = "127.0.0.1";
settings.Connection.I2pSamPort = 7656;
```

One SAM 3.1 STREAM session per engine carries HTTP tracker announces and incoming/outgoing peer
streams. Compact destination hashes and non-compact destination lists are supported. The router
performs I2P name lookups. Java I2P users must enable SAM in their router; i2pd normally enables it.
I2P torrents keep ordinary IP peers, public trackers, DHT, local discovery, IP peer exchange and
file web seeds out of their traffic by default. `I2pAllowMixedNetworks` explicitly permits ordinary
IP peers and trackers. I2P datagram trackers, I2P DHT and `i2p_pex` are not implemented.

SSL torrent tests use PeerSharp peers with real TLS sockets, and I2P tests use a simulated SAM
router. Compatibility with external SSL peers and live Java I2P/i2pd networks has not been validated.

Magnet metadata accepts up to 30 MiB by default, matching libtorrent 2.0.11. Adjust
`settings.Transfer.MaxMetadataSizeBytes` for the deployment. Torrent parsing uses a separate,
bounded metadata profile, so large piece-hash strings do not hit ordinary peer-message limits.

### Adding Peers Directly

Discovery finds peers on its own, but some are only reachable if you say so — a machine on the same
LAN, a known seedbox, or a second instance during testing.

```csharp
int accepted = torrent.Peers.Add([new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51413)]);
```

Offered rather than forced: each address still passes the blocklist, the connection limits and the
same duplicate checks as any other candidate, and joins the ordinary connection queue rather than
pre-empting it. The return value counts addresses accepted as new candidates, not connections made.

### The Sample Client

`samples/PeerSharp.Cli` is a working client and the harness most of this engine's diagnosis is done
with. It runs as its own process, so `dotnet-counters`, `dotnet-gcdump` and `dotnet-trace` can attach
to a real workload rather than a test host.

```bash
dotnet run --project samples/PeerSharp.Cli -- <torrent-file|magnet> -o ./downloads --diagnostics
```

It takes several torrents at once and covers the things worth watching over a long run: `--seed` to
keep going after completion, `--log <file>` to send full logging to disk while the console keeps
reports, `--resume <dir>` to save and reload resume data across restarts, `--metadata-only` to time a
magnet's metadata fetch and stop, `--run-for <s>` for an unattended run that exits cleanly, and
`--down`/`--up` rate caps. Running it with no arguments prints the full list.

## WebTorrent

PeerSharp.WebTorrent is an optional extension package that adds peer support over WebRTC data channels. Install it only in applications that need browser/WebTorrent interop; the core `PeerSharp` package has no dependency on RtcForge or WebRTC.

```bash
dotnet add package PeerSharp.WebTorrent --version 5.2.0
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
- There is a demo harness at [samples/PeerSharp.WebTorrent.Demo/Program.cs](https://github.com/ligenq/PeerSharp/blob/main/samples/PeerSharp.WebTorrent.Demo/Program.cs) for controlled interop and soak testing.
- The `PeerSharp.WebTorrent` logger category emits reconnect, pending-peer expiry, and signaling lifecycle information. For rollout, capture this category at `Information` or `Debug`.

## Testing

Unit, integration, architecture and property-based tests run in CI. To run the core suite locally,
use the SDK selected by `global.json` (10.0.400 or a compatible patch):

```shell
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Release
```

The suites that need more - coverage-guided fuzzing, interop against real client binaries, live
DHT probes, and soak runs against a real swarm - are described in
[tests/README.md](https://github.com/ligenq/PeerSharp/blob/main/tests/README.md), along with how
to read what they report.

For repeatable traffic correctness checks over real loopback sockets, run
`./tools/Invoke-TrafficAudit.ps1 -Repeat 3` in PowerShell. See the
[traffic audit guide](tools/TRAFFIC-AUDIT.md) for saved transcripts, extended scenarios, and packet-capture instructions.
On Windows with Npcap/Wireshark, add `-CapturePackets` to retain loopback PCAPNG files and decoded
packet summaries alongside those checks.

For a bounded public-swarm capture using a torrent you supply, including a uTP-only run, see
the [live traffic audit guide](tools/LIVE-TRAFFIC-AUDIT.md). This requires Windows and Npcap/Wireshark.

### Development quality gates

The repository pins its .NET SDK, treats compiler and recommended analyzer warnings as errors,
audits direct and transitive NuGet dependencies during restore, and verifies formatting in CI.
Pull requests build and test on Linux and Windows, publish Cobertura coverage with an 80% line and
branch floor for each shipping assembly, validate trimmed output, replay the fuzz corpus, and keep
every benchmark fixture executable. CodeQL and dependency review cover code and supply-chain
changes. Bounded fuzzing, real-client interoperability and mutation analysis run through manual
workflow triggers. CodeQL also runs on pushes and pull requests.

Shipping packages are validated against the NuGet baseline configured in each package project
and the approved public-API snapshot. Before opening a pull request, run at least:

```powershell
dotnet restore PeerSharp.slnx
dotnet format PeerSharp.slnx --verify-no-changes --no-restore
dotnet build PeerSharp.slnx --configuration Debug --warnaserror
dotnet test --project tests/PeerSharp.Tests/PeerSharp.Tests.csproj --configuration Debug
dotnet test --project tests/PeerSharp.WebTorrent.Tests/PeerSharp.WebTorrent.Tests.csproj --configuration Debug
dotnet pack src/PeerSharp/PeerSharp.csproj --configuration Release
dotnet pack src/PeerSharp.WebTorrent/PeerSharp.WebTorrent.csproj --configuration Release
```

## Supported BEPs

PeerSharp aims for high compatibility with the BitTorrent ecosystem:

| BEP | Title | Status |
|-----|-------|--------|
| 3   | The BitTorrent Protocol Specification | Supported |
| 5   | DHT Protocol | Supported |
| 6   | Fast Extension | Supported |
| 7   | IPv6 Tracker Extension | Supported for `peers6` responses. The `ipv6` announce parameter is deliberately not sent - the BEP discourages it - so a tracker only records the family we announce over |
| 9   | Extension for Peers to Send Metadata Files | Supported |
| 10  | Extension Protocol | Supported |
| 11  | Peer Exchange (PEX) | Supported |
| 12  | Multitracker Metadata Extension | Supported |
| 14  | Local Service Discovery | Supported |
| 15  | UDP Tracker Protocol | Supported |
| 16  | Superseeding | Supported, via `ITorrent.SuperSeeding` or `AddTorrentOptions.SuperSeeding` |
| 19  | WebSeed - HTTP/FTP Seeding (GetRight style) | HTTP/HTTPS web seeds supported; FTP is not supported |
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
| 46  | Updating Torrents Via DHT Mutable Items | Supported, including `xs=urn:btpk:` magnet links |
| 47  | Padding Files and Extended File Attributes | Supported, including padding-file creation and download skipping |
| 48  | Tracker Protocol Extension: Scrape | Supported |
| 51  | DHT Infohash Indexing | Supported, both as responder and as crawler |
| 52  | The BitTorrent Protocol Specification v2 | Supported |
| 53  | Magnet URI Extension - Select Specific File Indices for Download | Supported |
| 54  | The lt_donthave Extension | Supported, sent when a piece turns out to be unreadable |
| 55  | Holepunch Extension | Supported |

### A note on BEP 41 and packet size

BEP 41 makes UDP announces longer than the 98 bytes of BEP 15 alone. That is the extension point the
BEP defines, and implementations read fixed offsets, so trackers without support ignore the trailing
bytes. It is on by default because the alternative is a silent failure — a passkey that never arrives,
a working socket and no peers. Set `Settings.SendUdpTrackerUrlData = false` if a particular tracker
rejects the longer packet.

## Changes

[CHANGELOG.md](https://github.com/ligenq/PeerSharp/blob/main/CHANGELOG.md) lists what each release
changed, with the upgrade notes first — the behaviour that moves without a signature moving with it.

## Known Gaps

[FUTURE_IMPROVEMENTS.md](https://github.com/ligenq/PeerSharp/blob/main/FUTURE_IMPROVEMENTS.md)
tracks deferred work and the evidence or consumer requirements needed before implementing it.

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
