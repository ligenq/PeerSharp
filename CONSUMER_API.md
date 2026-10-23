# Application integration APIs

These APIs let applications use the engine's torrent semantics without maintaining their own
copies. They are available in PeerSharp 5.0.0 and later. See [the changelog](CHANGELOG.md)
for migration notes when upgrading from 4.0.0.

## Identity and magnet links

```csharp
using PeerSharp.Core;

bool same = TorrentIdentity.SameTorrent(firstTorrent, secondTorrent);
var found = engine.GetTorrent(hash); // Full v1/v2 hashes and truncated v2 routing hashes.

string magnet = MagnetLink.FromTorrent(torrent).ToString();
string withTrackers = MagnetLink.FromTorrent(torrent, includeTrackers: true).ToString();
var fromFile = MagnetLink.FromTorrentFile(torrentFile);
var v2Only = MagnetLink.Create(infoHashV2: fullSha256Hash, displayName: "Example");
```

Trackers are omitted by default. A hybrid link includes both `btih` and `btmh` topics; a v2 link
uses the full SHA-256 multihash, never a truncated hash labelled as v1. Creating a link requires at
least one non-empty hash, but does not require downloaded metadata. This creates a link to the
current torrent, not a self-updating publisher link.

`SameTorrent` returns false for null arguments and ignores absent hash versions. An instance with
no known hash still matches itself. Sharing a known hash is not a transitive relation between
partially known torrents, so do not use this predicate as a dictionary equality comparer.
`InfoHash` and `TorrentFile` retain ordinary value equality for collections.

## Polling statistics

```csharp
var transfer = torrent.GetTransferStats();
Console.WriteLine($"{transfer.DownloadSpeed} B/s; {transfer.Downloaded} bytes received");

var stats = engine.GetStats();
Console.WriteLine($"Currently registered: {stats.TotalDownloaded}");
Console.WriteLine($"Including removed torrents: {stats.LifetimeDownloaded}");
```

Snapshots are immutable values. Each field is read independently, so concurrent transfers can
advance counters during a read. Rates use the engine's latest sample; totals and peer counts are
read on demand. Lifetime totals are scoped to an engine instance and include restored torrent
counters, matching the diagnostic meter. They are not a durable "bytes since application launch"
counter. The existing `EngineStats` constructor and deconstruction remain unchanged.

## Inspecting proxy capabilities

```csharp
var udp = settings.Proxy.GetUdpCapabilities();
// Present udp.SupportsDht, udp.SupportsUtp, and udp.SupportsUdpTrackers in settings UI.
```

HTTP cannot carry proxied UDP. DHT follows a configured proxy, while peers and trackers follow
their individual proxy flags. DHT and uTP share a socket: enabled but unsupported DHT prevents
that shared listener starting even when uTP alone would be permitted. Capability reporting
does not probe a server; a SOCKS5 server must still implement UDP association and be reachable.
The application decides whether to disable a feature, explain the restriction, or reject the
configuration. PeerSharp continues to refuse unsupported routing rather than silently bypassing it.

## Responsibilities that stay with the application

Categories, localized labels, notifications, clipboard access, Transmission/MCP schemas, download
folder naming, watch folders, bandwidth schedules, and default seeding policy remain host choices.
HTTP retrieval can use the host's configured `HttpClient` followed by `TorrentFile.Parse`.

Some Peerfluence compatibility code is already covered by existing PeerSharp APIs: metadata
preview has `GetMagnetMetadataWithProgressAsync`; downloaded metadata can be exported through
`ITorrent.ExportTorrentFile`; self-updating links have `ResolveSelfUpdatingMagnetAsync` before
adding a resolved torrent. Those capabilities do not need another application-specific wrapper
inside the engine.
