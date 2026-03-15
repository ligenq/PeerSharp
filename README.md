<p align="center">
  <img src="src/PeerSharp/application-icon.png" alt="PeerSharp" width="128" />
</p>

<h1 align="center">PeerSharp</h1>

<p align="center">
  <a href="https://www.nuget.org/packages/PeerSharp"><img src="https://img.shields.io/nuget/v/PeerSharp.svg" alt="NuGet Version" /></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT" /></a>
</p>

PeerSharp is a high-performance, modern BitTorrent engine for .NET 10+.

## Key Features

- **Full BEP Support:** Implements 20+ BitTorrent Extension Protocols (see [Supported BEPs](#supported-beps)).
- **Hybrid Networking:** Native support for both TCP and uTP (BEP 29) with automatic congestion control.
- **DHT & Peer Discovery:** Full Mainline DHT (BEP 5), Local Service Discovery (BEP 14), Peer Exchange (PEX), and UDP/HTTP Tracker support.
- **Magnet Links:** Fast metadata exchange (BEP 9) allowing torrent starts from magnet links alone.
- **Streaming Engine:** Integrated HTTP streaming server for real-time media playback while downloading.
- **Protocol Encryption:** MSE (Message Stream Encryption) with configurable enforcement modes.
- **NAT Traversal:** UPnP, NAT-PMP, and Holepunch (BEP 55) for connectivity behind NATs.
- **Bandwidth Control:** Per-torrent and global upload/download/disk I/O rate limiting.
- **Proxy Support:** SOCKS5 and HTTP proxy support with authentication.
- **IP Blocklist & GeoIP:** Block peers by IP range or country.
- **Optimized I/O:** Zero-copy Bencoding, pooled buffers, block caching, and asynchronous disk I/O designed for high-throughput scenarios.
- **Enterprise-Grade Testing:** Rigorous validation using **Microsoft Coyote** for concurrency testing, architecture tests for design integrity, and fuzzing for robustness.

## Getting Started

### Installation

```bash
dotnet add package PeerSharp
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

### Streaming

```csharp
// Open a seekable stream for media playback
var stream = await torrent.OpenStreamAsync(fileIndex: 0);
```

## Supported BEPs

PeerSharp aims for high compatibility with the BitTorrent ecosystem:

| BEP | Title | Status |
|-----|-------|--------|
| 3   | The BitTorrent Protocol Specification (TCP) | Supported |
| 5   | DHT Protocol | Supported |
| 6   | Fast Extension | Supported |
| 9   | Extension for Handling Metadata Files | Supported |
| 10  | Extension Protocol | Supported |
| 12  | Multitracker Metadata Extension | Supported |
| 14  | Local Service Discovery | Supported |
| 15  | UDP Tracker Protocol | Supported |
| 16  | Super-seeding | Supported |
| 19  | Web Seed Protocol | Supported |
| 20  | Peer ID Conventions | Supported |
| 23  | Compact Peer Lists | Supported |
| 27  | Private Torrents | Supported |
| 29  | uTP - Micro Transport Protocol | Supported |
| 30  | Merkle Hash Torrent | Supported |
| 32  | Merkle Tree (revised) | Supported |
| 33  | DHT Scrape | Supported |
| 40  | Canonical Peer Priority | Supported |
| 42  | DHT Security Extension | Supported |
| 47  | Padding Files | Supported |
| 48  | Tracker Returns Compact Peer Lists | Supported |
| 52  | BitTorrent Protocol v2 | Planned |
| 55  | Holepunch Extension | Supported |

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
