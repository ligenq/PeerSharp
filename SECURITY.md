# Security

## Reporting a vulnerability

Report vulnerabilities privately through the repository's GitHub **Security** tab by selecting
**Report a vulnerability**. Do not open a public issue containing exploit details.

PeerSharp parses and exchanges data with untrusted peers, trackers, DHT nodes, WebTorrent signaling
servers, torrent files, and magnet links. Reports involving malformed protocol input, path traversal,
resource exhaustion, cryptographic misuse, proxy bypass, unsafe deserialization, or concurrency bugs
that compromise integrity or availability are in scope.

Please include the affected version, reproduction steps or a minimal fixture, expected impact, and
any relevant operating-system or network conditions. Only the latest release is supported.

## Release integrity

Release packages include symbol packages, SPDX software bills of materials, and GitHub build
provenance attestations. NuGet publishing uses short-lived OpenID Connect credentials rather than a
stored long-lived API key.
