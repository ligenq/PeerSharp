# Bounded real-download packet audit

This runs the local engine directly with an operator-supplied torrent, a separate retained download
directory, a 2 MiB/s download limit and 128 KiB/s upload limit. `-Transport Tcp` (the default) or
`-Transport Utp` selects the peer transport, with the other peer transport disabled. It uses plaintext
so the wire messages can be independently checked. Web seeds, LSD, incoming TCP, session persistence,
and automatic router port mapping are disabled. It leaves the system's routing and VPN settings
unchanged. HTTP(S) tracker discovery may still use TCP when peer TCP is disabled.

On Windows with Npcap, Wireshark, and .NET 10 installed:

```powershell
./tools/Invoke-LiveTrafficAudit.ps1 `
  -TorrentPath 'C:\path\image.torrent' `
  -Interface '\Device\NPF_{capture-interface-guid}'

# Repeat over uTP, with incoming and outgoing peer TCP disabled:
./tools/Invoke-LiveTrafficAudit.ps1 `
  -TorrentPath 'C:\path\image.torrent' `
  -Interface '\Device\NPF_{capture-interface-guid}' -Transport Utp
```

Use `dumpcap -D` to choose the interface carrying the download. When a VPN carries it, capture its
virtual interface rather than the encrypted outer tunnel on the physical interface. The script
shares the isolated audit's single-run guard, and `-NoBuild` reuses the Release test binary.

Discovery runs for at most 30 seconds and selects up to eight currently connected IPv4 peers.
The torrent stops, the coordinator starts an endpoint-filtered capture, and the torrent restarts
with those peers reintroduced. Normal peer discovery continues, so the capture covers a subset of
the live download. Incoming peer payload is sampled every 500 ms; the run stops at approximately
64 MiB received or 90 seconds. In-flight data and sampling allow a small byte-budget overshoot.
Capture stops automatically after 105 seconds or 200,000 kB. All completed pieces are then rechecked
from disk. Downloads, samples, logs, and PCAPNG files are retained, never written into the source
torrent's download directory. A sparse or preallocated data file can have the torrent's full nominal
length despite only part of its content having been downloaded.

The script checks for an actually executed download test, nonempty capture, no capture drops, and
capture continuing through the stopped-download marker. It writes a capture-health summary. Its
failure paths stop only its own runner and capture processes. Existing download/session state is
not imported. The default artifact parent is `C:\temp\peersharp-live-audit`.

Analyze the retained plaintext streams using Python 3 and TShark:

```powershell
python tools/analyze-peer-capture.py `
  'C:\capture\traffic.pcapng' 'C:\path\image.torrent' `
  --local-address 'your-interface-IP' --output 'C:\capture\protocol-analysis.json'

python -B -m unittest discover -s tools -p test_analyze_peer_capture.py
```

For uTP, reconstruct its packet sequence (including retransmissions and wraparound) first, then
run the same peer-message and piece-hash checker:

```powershell
python -B tools/reassemble-utp.py 'C:\capture\traffic.pcapng' `
  --output-directory 'C:\capture\streams'
python -B tools/analyze-peer-capture.py `
  'C:\capture\traffic.pcapng' 'C:\path\image.torrent' `
  --local-address 'your-interface-IP' --reassembled-streams 'C:\capture\streams' `
  --output 'C:\capture\protocol-analysis.json'
python -B -m unittest discover -s tools -p 'test_*.py'
```

uTP reconstruction follows [BEP 29](https://www.bittorrent.org/beps/bep_0029.html). Only flows with
a captured SYN can be anchored; earlier flows are counted separately. Missing sequence gaps are
never skipped, repeated application bytes are suppressed, and conflicting retransmission payloads
are reported. Nonstandard but bounded SACK lengths are recorded without discarding their packet's
data. A short mask from a remote peer is not attributed to PeerSharp. The uTP capture filter also
includes TCP to detect unintended fallback, and the live test checks every sampled peer's transport.

The analyzer uses TShark's TCP-stream reconstruction and its own BitTorrent frame parser. It checks
info hashes, outgoing block ranges, same-peer outstanding duplicates, choke/allowed-fast rules,
fast-extension negotiation, initial bitfield order, and request/cancel/reply matching. Captured
complete pieces are independently SHA-1 checked against the original torrent. Piece or reject
responses after cancellation are counted separately, as permitted by [BEP 6](https://www.bittorrent.org/beps/bep_0006.html).
Pending requests or partial final frames at deliberate shutdown are reported, not assumed to be
bugs. A stream without a complete handshake cannot establish protocol conformance. This is a
diagnostic checker, not a complete implementation of every BitTorrent extension.

TCP retransmission, reordering and zero-window indications alone do not identify engine defects.
See [Wireshark's TCP analysis guide](https://www.wireshark.org/docs/wsug_html_chunked/ChAdvTCPAnalysis.html).
Encrypted sessions require a separate investigation. These bounded runs do not measure unrestricted
maximum throughput or prove every congestion-control and packet-loss recovery path correct.
