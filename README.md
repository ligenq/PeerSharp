# PeerSharp

[![NuGet Version](https://img.shields.io/nuget/v/PeerSharp.svg)](https://www.nuget.org/packages/PeerSharp)
[![Build Status](https://github.com/Peerfluence/PeerSharp/actions/workflows/build.yml/badge.svg)](https://github.com/Peerfluence/PeerSharp/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

PeerSharp is a high-performance, modern BitTorrent engine for .NET.

## 🚀 Key Features

- **Full BEP Support:** Implements core and extended BitTorrent Extension Protocols (see [Supported BEPs](#-supported-beps)).
- **Hybrid Networking:** Native support for both TCP and uTP (BEP 29) with automatic congestion control.
- **DHT & Peer Discovery:** Full Mainline DHT (BEP 5) implementation, Local Service Discovery (LSD), and UDP/HTTP Tracker support.
- **Magnet Links:** Fast metadata exchange (BEP 9) allowing torrent starts from magnet links alone.
- **Streaming Engine:** Integrated HTTP streaming server for real-time media playback while downloading.
- **Optimized I/O:** Zero-copy Bencoding, pooled buffers, and asynchronous disk I/O designed for high-throughput scenarios.
- **Enterprise-Grade Testing:** Rigorous validation using **Microsoft Coyote** for concurrency testing, architecture tests for design integrity, and extensive fuzzing for robustness.

## 🛠 Architecture

PeerSharp is designed with a modular, interface-driven architecture to ensure testability and extensibility:

- **ClientEngine:** The central orchestrator managing multiple torrent sessions.
- **PiecePicker:** Advanced logic for piece selection (rarest-first, sequential, streaming modes).
- **PieceWriter/Storage:** Abstracted disk I/O layer with sparse file support and block caching.
- **BEncoding:** A high-performance, allocation-aware parser and writer for the BitTorrent data format.
- **Alert System:** A centralized event bus for real-time monitoring of engine state and statistics.

## 📦 Getting Started

### Installation

Add the PeerSharp NuGet package to your project:

```bash
dotnet add package PeerSharp
```

### Basic Usage

```csharp
using PeerSharp.Clients;
using PeerSharp.Config;

// Initialize the engine
var engine = ClientEngineFactory.Create();
await engine.StartAsync();

// Add a torrent from a file or magnet link
var options = new AddTorrentOptions { SavePath = "./downloads" };
var torrent = await engine.AddTorrentAsync("my_file.torrent", options);

// Monitor progress via alerts
engine.Alerts.TorrentAdded += (s, e) => Console.WriteLine($"Started: {e.Torrent.Name}");
torrent.Events.PieceChecked += (s, e) => Console.WriteLine($"Progress: {e.Progress:P2}");

// Wait for completion or perform other actions
```

## 📜 Supported BEPs

PeerSharp aims for high compatibility with the BitTorrent ecosystem:

| BEP | Title | Status |
|-----|-------|--------|
| 3   | The BitTorrent Protocol Specification (TCP) | ✅ |
| 5   | DHT Protocol | ✅ |
| 6   | Fast Extension | ✅ |
| 9   | Extension for Handling Metadata Files | ✅ |
| 10  | Extension Protocol | ✅ |
| 12  | Multitracker Metadata Extension | ✅ |
| 14  | Local Service Discovery | ✅ |
| 15  | UDP Tracker Protocol | ✅ |
| 27  | Private Torrents | ✅ |
| 29  | uTP - Micro Transport Protocol | ✅ |
| 52  | BitTorrent Protocol v2 | 🏗️ (Planned) |

## 🧪 Robustness & Quality

PeerSharp is built with a "reliability-first" mindset:

- **Concurrency Testing:** Uses [Microsoft Coyote](https://microsoft.github.io/coyote/) to detect and fix non-deterministic race conditions in the distributed state machine.
- **Architecture Tests:** Enforces strict layering and design patterns (e.g., disposable ownership, thread-safety primitives) via automated tests.
- **Performance Profiling:** Continuous monitoring of allocations and throughput to maintain a low-overhead profile.

## 📄 License

Distributed under the MIT License. See `LICENSE` for more information.

