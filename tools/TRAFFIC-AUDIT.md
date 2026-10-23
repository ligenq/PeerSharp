# Traffic correctness audit

Run from the PeerSharp checkout in PowerShell with the .NET 10 SDK:

```powershell
./tools/Invoke-TrafficAudit.ps1 -Repeat 3
./tools/Invoke-TrafficAudit.ps1 -Extended
./tools/Invoke-TrafficAudit.ps1 -CapturePackets -Repeat 3
```

`-OutputDirectory C:\temp\peersharp-audit` chooses the artifact parent; each invocation creates a
unique run directory. `-NoBuild` uses the existing Release test binaries. A build failure, failed
test, or a filter that ran zero tests makes the script fail. The default is six short audit cases;
`-Extended` includes the existing synthetic handshake, metadata, hostile-input, redundancy, and
opening-sequence tests as well.

The audit uses real loopback TCP sockets and generated four-piece data. Its peer encodes and reads
the wire independently of PeerSharp's protocol codec. It checks:

- Repeated `HAVE`, including after `BITFIELD` or `HAVE_ALL`, represents one source per connection.
- `lt_donthave`, repeated retractions, re-advertisement, and disconnect keep the picker's counts
  consistent with the connected peers' piece maps.
- Every tested upload request receives one matching response with the correct bytes, including
  short and unaligned ranges. Requests are split across writes to exercise stream framing.
- Fast-extension messages are not sent to peers that did not negotiate them.

Each audit case retains `traffic.json`, with explicit messages sent by the test, frames actually
received from the engine, and observed source counts. `tests.log` and `tests.xml` associate each
capture with its test and result; `summary.json` records repeat counts and exit codes. The sent list
contains explicit scenario frames, not the synthetic peer's automatic handshakes. Frame payloads
are hex-encoded generated data. The files and fixtures are retained for inspection.

Retention is deliberate, not unconditional: the cases keep their directory when the script's
`PEERSHARP_WIRE_AUDIT_DIR` points at a run directory, and when a case fails. A plain `dotnet test`
of the suite sets no such directory, so a passing case deletes its own transcript and seeded
payload instead of leaving one directory per case under the system temp directory. Set
`PEERSHARP_WIRE_AUDIT_DIR` to keep captures from a run outside the script.

Without `-CapturePackets`, these are application-message transcripts, **not PCAP files or passive
monitoring of arbitrary Peerfluence traffic**. They do not expose TCP headers, TCP retransmissions, uTP datagrams, encrypted
payloads, or every possible protocol interaction. The extended suite exercises some encryption and
metadata paths separately; it does not turn this transcript format into a packet capture.

## Npcap packet capture on Windows

With Wireshark/TShark and Npcap installed, `-CapturePackets` retains a real `traffic.pcapng` per
iteration alongside the transcripts. The six audit cases use `127.0.0.42`, so the loopback capture
filter `tcp and host 127.0.0.42` excludes other local addresses and physical/VPN interface traffic.
Connection observations in each `traffic.json` include the synthetic peer's listening port, which
identifies that case's TCP stream in the packet capture. No Peerfluence process needs to be running.

Capture starts before the tests and stops automatically after `-CaptureSeconds` (default 15).
Increase that bound on a slower machine. `-WiresharkDirectory` overrides the default installation
path. The audit fails if capture ends before the tests finish, reports drops, lacks the expected
connections/handshakes, or TShark reports malformed packets or missing TCP segments. Retransmission
and reset counts are recorded for inspection; those events alone do not prove a protocol bug.
Run `-Extended` separately from `-CapturePackets`: the extended tests use their usual loopback
address and include intentional hostile input and lengthy timeout checks. Combining the two modes
is rejected rather than producing a partial capture of the extended suite.
The script rejects overlapping audit invocations; also avoid running synthetic tests directly
during capture, since they may use the same dedicated address.

`packets.tsv` contains TShark's two-pass dissection with TCP and BitTorrent reassembly enabled;
`packets-summary.json` records counts and capture health, and `capture.log` contains Npcap's drop
statistics. Open `traffic.pcapng` in Wireshark to inspect or follow a TCP stream. The decoded fields
are documented in the [Wireshark BitTorrent display-filter reference](https://www.wireshark.org/docs/dfref/b/bittorrent.html).
These controlled plaintext TCP cases do not establish real-swarm, encrypted, DHT, or uTP correctness.
For an endpoint-filtered sample of an operator-supplied real download, see the
[live traffic audit](LIVE-TRAFFIC-AUDIT.md).
